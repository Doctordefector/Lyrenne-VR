using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// Which artwork belongs on screen, decided only by which track each image is for, so images that
/// arrive late or out of order (rapid skipping) can never end up on the wrong song.
///
/// Lyrenne publishes every new song twice: first with its own logo as a placeholder, then again
/// once the real cover has downloaded. A thumbnail is therefore never final: the latest one for
/// the current track wins, except that a full-size cover looked up for that track beats any
/// thumbnail. Earlier code cached the first thumbnail as final and so could keep the placeholder.
/// </summary>
public sealed class ArtState
{
    readonly object _lock = new();
    readonly Dictionary<string, SKImage> _fullSize = new();
    readonly HashSet<string> _lookedUp = new();
    string _track = "";
    SKImage? _thumb;

    public SKImage? Current
    {
        get
        {
            lock (_lock) return _fullSize.TryGetValue(_track, out var big) ? big : _thumb;
        }
    }

    public string Track
    {
        get
        {
            lock (_lock) return _track;
        }
    }

    /// <summary>The track changed (or was re-announced). Drops the old song's thumbnail.</summary>
    public void SetTrack(string key)
    {
        lock (_lock)
        {
            if (key == _track) return;
            _track = key;
            _thumb = null;
        }
    }

    /// <summary>A thumbnail read for <paramref name="key"/>. Ignored if the track moved on meanwhile.</summary>
    public bool OfferThumbnail(string key, SKImage? image)
    {
        lock (_lock)
        {
            if (key != _track || image == null) return false;
            _thumb = image;
            return true;
        }
    }

    /// <summary>True once per track: whether the caller should look up a full-size cover.</summary>
    public bool ShouldLookUp(string key)
    {
        lock (_lock)
        {
            if (_fullSize.ContainsKey(key)) return false;
            return _lookedUp.Add(key);
        }
    }

    /// <summary>A full-size cover found for <paramref name="key"/>, kept for when the song comes back.</summary>
    public bool OfferFullSize(string key, SKImage? image)
    {
        lock (_lock)
        {
            if (image == null) return false;
            if (_fullSize.Count > 64)
            {
                _fullSize.Clear();
                _lookedUp.Clear();
            }
            _fullSize[key] = image;
            return key == _track;
        }
    }
}
