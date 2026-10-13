namespace AFMediaBar.Classes.Services.Layout;

/// <summary>
/// 封面框的尺寸策略：高度由布局决定，宽度按封面自身的宽高比算，因此非正方形封面（视频封面、竖版海报）不再被裁切。
///
/// 比例超出允许范围时只把盒子夹到边界，并告诉调用方"需要留白"——与其把封面裁掉一块，不如按原比例缩放显示。
/// Artwork box sizing: the height comes from the layout while the width follows the artwork's own aspect, so a non-square cover (a video's
/// cover, a portrait poster) is no longer cropped.
///
/// An aspect beyond the allowed range clamps the box and reports that letterboxing is needed: scaling the cover down is preferable to
/// cutting a piece off it.
/// </summary>
public static class ArtworkBoxPolicy
{
    /// <summary>封面框允许的最小宽高比（高为 1）：更窄的封面按这个比例留白显示。/ Smallest allowed aspect of the artwork box, with height as one; narrower covers are letterboxed at this ratio.</summary>
    public const double MinimumAspect = 0.6;

    /// <summary>封面框允许的最大宽高比（高为 1）：更宽的封面按这个比例留白显示。/ Largest allowed aspect of the artwork box, with height as one; wider covers are letterboxed at this ratio.</summary>
    public const double MaximumAspect = 1.8;

    /// <summary>
    /// 算出封面框的宽度与是否需要留白。封面尺寸无效时按正方形处理，占位音符因此保持居中。
    /// Computes the artwork box's width and whether letterboxing is required. An unusable artwork size falls back to a square so that the
    /// placeholder note stays centred.
    /// </summary>
    /// <param name="height">封面框高度（DIP），由布局决定。/ Artwork box height in DIP, decided by the layout.</param>
    /// <param name="pixelWidth">封面像素宽度。/ Artwork width in pixels.</param>
    /// <param name="pixelHeight">封面像素高度。/ Artwork height in pixels.</param>
    public static ArtworkBox Resolve(double height, int pixelWidth, int pixelHeight)
    {
        if (!double.IsFinite(height) || height <= 0)
        {
            return new ArtworkBox(0, Letterbox: false);
        }

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return new ArtworkBox(height, Letterbox: false);
        }

        var aspect = (double)pixelWidth / pixelHeight;
        var clamped = Math.Clamp(aspect, MinimumAspect, MaximumAspect);
        return new ArtworkBox(height * clamped, Letterbox: Math.Abs(clamped - aspect) > 0.001);
    }
}

/// <summary>封面框的尺寸结果：宽度（DIP）与是否留白显示。/ Artwork box result: its width in DIP and whether the cover is letterboxed.</summary>
/// <param name="Width">封面框宽度（DIP）。/ Box width in DIP.</param>
/// <param name="Letterbox">是否按原比例缩放显示（比例被夹取，框内会留白）。/ Whether the cover is scaled to fit, leaving gaps inside the box because the ratio was clamped.</param>
public readonly record struct ArtworkBox(double Width, bool Letterbox);
