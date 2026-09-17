using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Stores the signed-in user's profile photo locally, keyed by Firebase UID.
///
/// There is no server-side avatar storage: the Worker's bug-report attachment
/// feature already hit this exact wall and was dropped after R2 turned out to
/// require an account-level activation step in the Cloudflare Dashboard that
/// could not be completed in this environment (see
/// <c>infra/cloudflare-worker/README.md</c>). The same blocker applies here,
/// so a photo set on one machine does not follow the account to another
/// install until that infrastructure exists.
///
/// Every save normalizes the source image to a square PNG capped at
/// <see cref="MaxDimension"/> px, regardless of what the user picked, so
/// on-disk size (and the eventual network cost, once sync exists) stays
/// bounded.
/// </summary>
public sealed class AccountAvatarStore
{
    public const int MaxDimension = 512;
    public const long MaxSourceFileBytes = 8 * 1024 * 1024;
    public const int MaxSourceDimension = 16_384;
    public const long MaxSourcePixels = 40_000_000;

    private readonly string directory;

    public AccountAvatarStore()
        : this(AppDataPaths.Combine("avatars"))
    {
    }

    internal AccountAvatarStore(string directory)
    {
        this.directory = directory;
    }

    /// <summary>Path the avatar for <paramref name="uid"/> is stored at, whether or not it exists yet.</summary>
    public string PathFor(string uid) => Path.Combine(directory, $"{Sanitize(uid)}.png");

    /// <summary>Loads the stored avatar for <paramref name="uid"/>, if any. Never throws.</summary>
    public BitmapImage? TryLoad(string uid)
    {
        var path = PathFor(uid);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = DecodeBounded(stream);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or ArgumentException or NotImplementedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the image at <paramref name="sourcePath"/>, center-crops it to a
    /// square, downsizes it to <see cref="MaxDimension"/> px if larger, and
    /// saves it as <paramref name="uid"/>'s avatar. Returns false — without
    /// writing anything — for a file that does not exist, is not a readable
    /// image, or exceeds the compressed-size or decoded-dimension limits.
    /// </summary>
    public bool TrySave(string uid, string sourcePath)
    {
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxSourceFileBytes)
            {
                return false;
            }

            var source = DecodeBounded(stream);

            var side = Math.Min(source.PixelWidth, source.PixelHeight);
            if (side <= 0)
            {
                return false;
            }

            var cropped = new CroppedBitmap(source, new Int32Rect(
                (source.PixelWidth - side) / 2,
                (source.PixelHeight - side) / 2,
                side,
                side));

            var scale = (double)MaxDimension / side;
            BitmapSource square = scale < 1
                ? new TransformedBitmap(cropped, new System.Windows.Media.ScaleTransform(scale, scale))
                : cropped;

            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(square));

            // Write-then-move so a save que falha no meio (disco cheio, processo
            // encerrado) nunca deixa um avatar parcial no lugar de um válido. O
            // nome temporário é único por gravação para que dois salvamentos
            // simultâneos do mesmo avatar não disputem o mesmo arquivo.
            var destination = PathFor(uid);
            var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var output = File.Create(temporary))
                {
                    encoder.Save(output);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                // Best-effort: o avatar já está durável após o Move, então uma
                // limpeza malsucedida não pode transformar um salvamento
                // concluído em falha.
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or ArgumentException or NotImplementedException)
        {
            return false;
        }
    }

    /// <summary>Removes the stored avatar for <paramref name="uid"/>, if any. Never throws.</summary>
    public void Delete(string uid)
    {
        try
        {
            File.Delete(PathFor(uid));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Firebase UIDs are opaque, URL-safe identifiers from a verified ID
    // token, not user input -- but keying a filesystem path on any external
    // string deserves the same defensive normalization regardless.
    private static string Sanitize(string uid) =>
        string.Concat(uid.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));

    internal static bool IsSourceSizeAllowed(int width, int height) =>
        width is > 0 and <= MaxSourceDimension &&
        height is > 0 and <= MaxSourceDimension &&
        (long)width * height <= MaxSourcePixels;

    private static BitmapImage DecodeBounded(FileStream stream)
    {
        if (stream.Length > MaxSourceFileBytes)
        {
            throw new InvalidDataException("Avatar image exceeds the encoded-size limit.");
        }

        // OnDemand reads the header without materializing the full pixel
        // buffer. Both imports and cached files pass through this gate.
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0
            || !IsSourceSizeAllowed(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight))
        {
            throw new InvalidDataException("Avatar image dimensions exceed the decode limit.");
        }

        var sourceWidth = decoder.Frames[0].PixelWidth;
        var sourceHeight = decoder.Frames[0].PixelHeight;
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (sourceWidth >= sourceHeight)
        {
            bitmap.DecodePixelHeight = Math.Min(sourceHeight, MaxDimension);
        }
        else
        {
            bitmap.DecodePixelWidth = Math.Min(sourceWidth, MaxDimension);
        }
        bitmap.EndInit();
        return bitmap;
    }
}
