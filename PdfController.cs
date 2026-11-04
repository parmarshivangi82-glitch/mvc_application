using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MVCapplication.Data;
using MVCapplication.Models;
using MVCapplication.Services;
using MVCapplication.ViewModels;
using UglyToad.PdfPig.Exceptions;

namespace MVCapplication.Controllers;

public sealed class PdfController(
    AppDbContext dbContext,
    IPdfExtractor pdfExtractor,
    IExcelExportService excelExportService,
    ILogger<PdfController> logger) : Controller
{
    private const long MaxUploadSizeBytes = 10 * 1024 * 1024;

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadSizeBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadSizeBytes)]
    public async Task<IActionResult> Upload(IFormFile? pdfFile, CancellationToken cancellationToken)
    {
        if (pdfFile is null || pdfFile.Length == 0)
        {
            ModelState.AddModelError(nameof(pdfFile), "Choose a PDF file to extract.");
            return View();
        }

        if (!string.Equals(Path.GetExtension(pdfFile.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(pdfFile), "Only PDF files are supported.");
            return View();
        }

        if (pdfFile.Length > MaxUploadSizeBytes)
        {
            ModelState.AddModelError(nameof(pdfFile), "The PDF must be 10 MB or smaller.");
            return View();
        }

        ExtractedDocument document;
        try
        {
            await using var stream = pdfFile.OpenReadStream();
            document = pdfExtractor.Extract(stream, Path.GetFileName(pdfFile.FileName), cancellationToken);
        }
        catch (PdfDocumentEncryptedException exception)
        {
            logger.LogWarning(exception, "Encrypted PDF extraction failed for {FileName}", Path.GetFileName(pdfFile.FileName));
            ModelState.AddModelError(nameof(pdfFile), "This PDF is password-protected and cannot be extracted without its password.");
            return View();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "PDF extraction failed for {FileName}", Path.GetFileName(pdfFile.FileName));
            ModelState.AddModelError(nameof(pdfFile), "We couldn't read that PDF. Check that it is valid and not password-protected.");
            return View();
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Could not read uploaded PDF {FileName}", Path.GetFileName(pdfFile.FileName));
            ModelState.AddModelError(nameof(pdfFile), "The uploaded file could not be read. Please try again.");
            return View();
        }

        var uploadedPdf = new UploadedPdf
        {
            FileName = Path.GetFileName(pdfFile.FileName),
            ExtractedDocumentJson = PdfDocumentSerializer.Serialize(document)
        };
        dbContext.UploadedPdfs.Add(uploadedPdf);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Could not save extraction results for {FileName}", uploadedPdf.FileName);
            ModelState.AddModelError(string.Empty, "The PDF was extracted, but its results could not be saved. Check the database connection and try again.");
            return View();
        }

        return View("Result", CreateViewModel(uploadedPdf, document));
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        var uploads = await dbContext.UploadedPdfs
            .AsNoTracking()
            .OrderByDescending(pdf => pdf.Id)
            .Select(pdf => new PdfHistoryItem { Id = pdf.Id, FileName = pdf.FileName })
            .ToListAsync(cancellationToken);

        return View(uploads);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var uploadedPdf = await FindPdfAsync(id, cancellationToken);
        if (uploadedPdf is null)
        {
            return NotFound();
        }

        var document = PdfDocumentSerializer.DeserializeOrLegacy(uploadedPdf.ExtractedDocumentJson, uploadedPdf.FileName);
        return View("Result", CreateViewModel(uploadedPdf, document));
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var uploadedPdf = await FindPdfAsync(id, cancellationToken);
        if (uploadedPdf is null)
        {
            return NotFound();
        }

        var document = PdfDocumentSerializer.DeserializeOrLegacy(uploadedPdf.ExtractedDocumentJson, uploadedPdf.FileName);
        var text = PdfDocumentSerializer.ToPlainText(document);
        var downloadName = $"{Path.GetFileNameWithoutExtension(uploadedPdf.FileName)}.txt";
        return File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", downloadName);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadJson(int id, CancellationToken cancellationToken)
    {
        var uploadedPdf = await FindPdfAsync(id, cancellationToken);
        if (uploadedPdf is null)
        {
            return NotFound();
        }

        var document = PdfDocumentSerializer.DeserializeOrLegacy(uploadedPdf.ExtractedDocumentJson, uploadedPdf.FileName);
        var json = PdfDocumentSerializer.Serialize(document);
        var downloadName = $"{Path.GetFileNameWithoutExtension(uploadedPdf.FileName)}.json";
        return File(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", downloadName);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadExcel(int id, CancellationToken cancellationToken)
    {
        var uploadedPdf = await FindPdfAsync(id, cancellationToken);
        if (uploadedPdf is null)
        {
            return NotFound();
        }

        var document = PdfDocumentSerializer.DeserializeOrLegacy(uploadedPdf.ExtractedDocumentJson, uploadedPdf.FileName);
        var downloadName = $"{Path.GetFileNameWithoutExtension(uploadedPdf.FileName)}.xlsx";
        return File(excelExportService.Export(document),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", downloadName);
    }

    private Task<UploadedPdf?> FindPdfAsync(int id, CancellationToken cancellationToken) =>
        dbContext.UploadedPdfs.AsNoTracking().FirstOrDefaultAsync(pdf => pdf.Id == id, cancellationToken);

    private static PdfResultViewModel CreateViewModel(UploadedPdf pdf, ExtractedDocument document) => new()
    {
        Id = pdf.Id,
        FileName = pdf.FileName,
        Document = document,
        PlainText = PdfDocumentSerializer.ToPlainText(document)
    };
}
