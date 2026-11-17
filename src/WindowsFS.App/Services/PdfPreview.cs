using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsFS.App.Services;

/// <summary>PDFium rendering only, from pinned memory. No file APIs, JavaScript, form actions, or save APIs.</summary>
internal static class PdfPreview
{
    private static readonly object Gate = new();

    public static FilePreview Read(MemoryStream source, int pageIndex, CancellationToken token)
    {
        // PDFium's global state is not thread-safe. Every native handle is closed before releasing the lock.
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            FPDF_InitLibrary();
            var pin = GCHandle.Alloc(source.GetBuffer(), GCHandleType.Pinned);
            nint document = 0, page = 0, bitmap = 0;
            try
            {
                document = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)source.Length, 0);
                if (document == 0) throw new InvalidDataException("Invalid or password-protected PDF.");
                var count = FPDF_GetPageCount(document);
                if (count <= 0) return new FilePreview("빈 PDF");
                var index = Math.Clamp(pageIndex, 0, count - 1);
                page = FPDF_LoadPage(document, index);
                if (page == 0) throw new InvalidDataException("Invalid PDF page.");
                var pageWidth = FPDF_GetPageWidth(page);
                var pageHeight = FPDF_GetPageHeight(page);
                if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
                    throw new InvalidDataException("Invalid PDF dimensions.");
                var scale = Math.Min(1600 / pageWidth, 1600 / pageHeight);
                var width = Math.Clamp((int)(pageWidth * scale), 1, 1600);
                var height = Math.Clamp((int)(pageHeight * scale), 1, 1600);
                bitmap = FPDFBitmap_CreateEx(width, height, 4, 0, 0); // BGRA
                if (bitmap == 0) throw new InvalidDataException("PDF bitmap allocation failed.");
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, uint.MaxValue);
                token.ThrowIfCancellationRequested();
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
                token.ThrowIfCancellationRequested();
                var stride = FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[checked(stride * height)];
                Marshal.Copy(FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                return new FilePreview(Image: image, PageIndex: index, PageCount: count);
            }
            finally
            {
                if (bitmap != 0) FPDFBitmap_Destroy(bitmap);
                if (page != 0) FPDF_ClosePage(page);
                if (document != 0) FPDF_CloseDocument(document);
                pin.Free();
                FPDF_DestroyLibrary();
            }
        }
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDF_DestroyLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern nint FPDF_LoadMemDocument64(nint buffer, nuint size, nint password);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern int FPDF_GetPageCount(nint document);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern double FPDF_GetPageWidth(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern double FPDF_GetPageHeight(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint buffer, int stride);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotation, int flags);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDF_ClosePage(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.StdCall)] private static extern void FPDF_CloseDocument(nint document);
}
