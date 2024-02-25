namespace RajceDownloader.Main;

using System;

public static class FileTypeRecognizer
{
    public static string GetFileExtension(Span<byte> fileHeader)
    {
        // JPEG
        if (fileHeader.Length >= 3 && fileHeader[0] == 0xFF && fileHeader[1] == 0xD8 && fileHeader[2] == 0xFF)
        {
            return ".jpg";
        }
        // PNG
        else if (fileHeader.Length >= 8 && fileHeader[0] == 0x89 && fileHeader[1] == 0x50 && fileHeader[2] == 0x4E && fileHeader[3] == 0x47
                 && fileHeader[4] == 0x0D && fileHeader[5] == 0x0A && fileHeader[6] == 0x1A && fileHeader[7] == 0x0A)
        {
            return ".png";
        }
        // GIF
        else if (fileHeader.Length >= 6 && fileHeader.Slice(0, 6).SequenceEqual(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }) 
                 || fileHeader.Slice(0, 6).SequenceEqual(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }))
        {
            return ".gif";
        }
        // BMP
        else if (fileHeader.Length >= 2 && fileHeader[0] == 0x42 && fileHeader[1] == 0x4D)
        {
            return ".bmp";
        }

        // Not recognized
        return null;
    }
}
