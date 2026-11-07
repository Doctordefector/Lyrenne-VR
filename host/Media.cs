using SkiaSharp;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using RepeatModeWin = Windows.Media.MediaPlaybackAutoRepeatMode;

namespace LyrenneVR;

public enum Repeat { Off, All, One }

/// <summary>Everything the panel draws, captured at one instant.</summary>
public sealed record NowPlaying(
    string Title,
    string Artist,
    string Album,
    SKImage? Art,
    bool Playing,
    TimeSpan Position,
    TimeSpan Duration,
    bool Shuffle,
    Repeat Repeat,
    bool CanSeek,
    string Source)
{
    public static readonly NowPlaying Empty = new("Nothing playing", "Start a song in Lyrenne", "", null, false,
        TimeSpan.Zero, TimeSpan.Zero, false, Repeat.Off, false, "");
}

/// <summary>
/// Reads and drives Windows' media transport controls. Lyrenne publishes its player there, so this
/// needs nothing from Lyrenne itself, and it keeps working with any other player as a fallback.
/// </summary>
public sealed class Media : IDisposable
{
    readonly Settings _settings;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    System.Threading.Timer? _recheck;
    int _picking;
    readonly object _lock = new();

    string _title = "", _artist = "", _album = "";
    SKImage? _art;
    PlaybackStatus _status = PlaybackStatus.Closed;
    TimeSpan _position, _duration;
    DateTimeOffset _positionAt = DateTimeOffset.UtcNow;
    bool _shuffle, _canSeek;
    Repeat _repeat;
    string _source = "";
    int _metadataVersion;

    // Optimistic state so a tap shows its result immediately, before the player reports back.
    TimeSpan? _pendingSeek;
    long _pendingSeekUntil;
    bool? _pendingPlaying;
    long _pendingPlayingUntil;

    public event Action? Changed;
    public string TrackKey => $"{_artist}\u0001{_title}";

    public Media(Settings settings) => _settings = settings;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = PickSessionAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = PickSessionAsync();
            await PickSessionAsync();
            // Windows' change events do not always arrive (seen live: Lyrenne opened after the
            // host and was never picked up), so look again every few seconds as well.
            _recheck = new System.Threading.Timer(_ => _ = PickSessionAsync(), null, 3000, 3000);
        }
        catch (Exception e)
        {
            Log.Write($"media controls unavailable: {e.Message}");
        }
    }

    async Task PickSessionAsync()
    {
        if (_manager == null || Interlocked.Exchange(ref _picking, 1) == 1) return;
        try
        {
            await PickSessionCoreAsync();
        }
        finally
        {
            Volatile.Write(ref _picking, 0);
        }
    }

    async Task PickSessionCoreAsync()
    {
        if (_manager == null) return;
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            var sessions = _manager.GetSessions();
            pick = sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("lyrenne", StringComparison.OrdinalIgnoreCase))
                   ?? sessions.FirstOrDefault(s => IsJava(s.SourceAppUserModelId));
            if (pick == null && !_settings.LyrenneOnly) pick = _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
        }
        catch (Exception e)
        {
            Log.Write($"listing media sessions failed: {e.Message}");
        }

        if (pick?.SourceAppUserModelId == _session?.SourceAppUserModelId && (pick == null) == (_session == null)) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnPlaybackInfo;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = pick;
        if (pick == null)
        {
            lock (_lock)
            {
                _title = _artist = _album = _source = "";
                _art = null;
                _status = PlaybackStatus.Closed;
                _metadataVersion++;
            }
            Changed?.Invoke();
            return;
        }
        Log.Write($"following media session {pick.SourceAppUserModelId}");
        pick.MediaPropertiesChanged += OnMediaProperties;
        pick.PlaybackInfoChanged += OnPlaybackInfo;
        pick.TimelinePropertiesChanged += OnTimeline;
        lock (_lock) _source = pick.SourceAppUserModelId;
        OnPlaybackInfo(pick, null);
        OnTimeline(pick, null);
        await LoadPropertiesAsync(pick);
    }

    // Lyrenne runs on the JVM; depending on how it was launched its session can carry java's id.
    static bool IsJava(string id) => id.Contains("java", StringComparison.OrdinalIgnoreCase);

    void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs? args) => _ = LoadPropertiesAsync(s);

    async Task LoadPropertiesAsync(GlobalSystemMediaTransportControlsSession s)
    {
        int version;
        lock (_lock) version = ++_metadataVersion;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null) return;
            SKImage? art = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var net = stream.AsStreamForRead();
                    using var copy = new MemoryStream();
                    await net.CopyToAsync(copy);
                    art = SKImage.FromEncodedData(copy.ToArray());
                }
                catch (Exception e)
                {
                    Log.Write($"artwork unreadable: {e.Message}");
                }
            }
            lock (_lock)
            {
                if (version != _metadataVersion || !ReferenceEquals(s, _session)) return;
                _title = props.Title ?? "";
                _artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
                _album = props.AlbumTitle ?? "";
                _art = art;
            }
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Write($"reading media properties failed: {e.Message}");
        }
    }

    void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs? _)
    {
        try
        {
            var info = s.GetPlaybackInfo();
            lock (_lock)
            {
                // Freeze the extrapolated position at the moment playback stops or starts.
                _position = CurrentPositionLocked();
                _positionAt = DateTimeOffset.UtcNow;
                _status = info.PlaybackStatus;
                _shuffle = info.IsShuffleActive ?? false;
                _repeat = info.AutoRepeatMode switch
                {
                    RepeatModeWin.Track => Repeat.One,
                    RepeatModeWin.List => Repeat.All,
                    _ => Repeat.Off,
                };
                _canSeek = info.Controls.IsPlaybackPositionEnabled;
                _pendingPlaying = null;
            }
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Write($"reading playback info failed: {e.Message}");
        }
    }

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs? _)
    {
        try
        {
            var t = s.GetTimelineProperties();
            lock (_lock)
            {
                _position = t.Position;
                _positionAt = t.LastUpdatedTime == default ? DateTimeOffset.UtcNow : t.LastUpdatedTime;
                // Some players stamp LastUpdatedTime oddly; never extrapolate from the future.
                if (_positionAt > DateTimeOffset.UtcNow) _positionAt = DateTimeOffset.UtcNow;
                _duration = t.EndTime - t.StartTime;
                if (_pendingSeek is { } want && Math.Abs((t.Position - want).TotalSeconds) < 2) _pendingSeek = null;
            }
        }
        catch (Exception e)
        {
            Log.Write($"reading timeline failed: {e.Message}");
        }
    }

    bool PlayingLocked => _pendingPlaying is { } p && Environment.TickCount64 < _pendingPlayingUntil ? p : _status == PlaybackStatus.Playing;

    TimeSpan CurrentPositionLocked()
    {
        if (_pendingSeek is { } seek && Environment.TickCount64 < _pendingSeekUntil) return seek;
        var pos = _position;
        if (_status == PlaybackStatus.Playing) pos += DateTimeOffset.UtcNow - _positionAt;
        if (_duration > TimeSpan.Zero && pos > _duration) pos = _duration;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    public NowPlaying Snapshot()
    {
        lock (_lock)
        {
            if (_session == null || string.IsNullOrEmpty(_title)) return NowPlaying.Empty with { Source = _source };
            return new NowPlaying(_title, _artist, _album, _art, PlayingLocked, CurrentPositionLocked(), _duration,
                _shuffle, _repeat, _canSeek && _duration > TimeSpan.Zero, _source);
        }
    }

    public void TogglePlay()
    {
        var s = _session;
        if (s == null) return;
        lock (_lock)
        {
            _pendingPlaying = !PlayingLocked;
            _pendingPlayingUntil = Environment.TickCount64 + 1500;
        }
        Fire(s.TryTogglePlayPauseAsync());
    }

    public void Next() { if (_session is { } s) Fire(s.TrySkipNextAsync()); }
    public void Previous() { if (_session is { } s) Fire(s.TrySkipPreviousAsync()); }

    public void Seek(TimeSpan to)
    {
        var s = _session;
        if (s == null) return;
        lock (_lock)
        {
            _pendingSeek = to;
            _pendingSeekUntil = Environment.TickCount64 + 2000;
        }
        Fire(s.TryChangePlaybackPositionAsync(to.Ticks));
    }

    public void ToggleShuffle()
    {
        var s = _session;
        if (s == null) return;
        bool want;
        lock (_lock) want = _shuffle = !_shuffle;
        Fire(s.TryChangeShuffleActiveAsync(want));
    }

    public void CycleRepeat()
    {
        var s = _session;
        if (s == null) return;
        Repeat want;
        lock (_lock) want = _repeat = _repeat switch { Repeat.Off => Repeat.All, Repeat.All => Repeat.One, _ => Repeat.Off };
        Fire(s.TryChangeAutoRepeatModeAsync(want switch
        {
            Repeat.All => RepeatModeWin.List,
            Repeat.One => RepeatModeWin.Track,
            _ => RepeatModeWin.None,
        }));
    }

    static void Fire(Windows.Foundation.IAsyncOperation<bool> op) =>
        _ = Task.Run(async () =>
        {
            try { await op; }
            catch (Exception e) { Log.Write($"media command failed: {e.Message}"); }
        });

    public void Dispose()
    {
        _recheck?.Dispose();
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnPlaybackInfo;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
    }
}
