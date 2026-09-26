# اتصال به نرم‌افزار میزبان

پروژهٔ `AutoReport.Core` یا DLL خروجی آن را به برنامهٔ .NET Framework 4.8 اضافه کنید. ساخت AnyCPU است. موتور Tesseract در فرایند جدا اجرا می‌شود تا DLL بومی به معماری فرایند میزبان گره نخورد؛ خود فایل اجرایی باید با Windows و CPU مقصد سازگار باشد.

```csharp
using AutoReport;
using System.Threading;

var profile = JsonFile.Read<ExtractionProfile>(@"config\extraction.default.json");
var reader = new TesseractReader(new TesseractOptions
{
    ExecutablePath = @"C:\YourApp\runtime\tesseract.exe",
    TessdataDirectory = @"C:\YourApp\runtime\tessdata",
    Languages = "eng",
    Threads = 1,
    TimeoutSeconds = 60
});
var engine = new AutoReportEngine(reader, profile);

// Only files for this one examination. Keep original images accessible to the reviewer.
Study study = await engine.ExtractAsync(
    examinationId,
    selectedImagePaths,
    cancellationToken);

// Show study.Observations next to the image named by SourceId / Sources.
// SourcePage.Lines contain bounding rectangles in the ORIGINAL image coordinates.
// Do not approve all candidates automatically: one key can have conflicting candidates.
study.Approve(selectedObservationId, authenticatedReviewerName);

// A manual correction should record its source, when available, and a specific reason.
study.SetReviewed("Obstetric.EFW", "2400", "g", authenticatedReviewerName,
    "Weight verified from original report page");

var templateProfile = JsonFile.Read<TemplateProfile>(centerBindingsPath);
var templates = new WordTemplate();
RenderResult preview = templates.RenderDraft(centerDocxPath, newDraftPath, templateProfile, study);

// After review of the entire narrative, patient association and every mapped field:
templates.RenderFinal(centerDocxPath, newFinalPath, templateProfile, study, authenticatedReviewerName);
JsonFile.Write(newStudyJsonPath, study);
```

این مثال در متد `async` برنامهٔ میزبان قرار می‌گیرد؛ متغیرهای نام‌گذاری‌شده متعلق به همان برنامه هستند. برای رابط WinForms/WPF، فراخوانی استخراج را در `Task.Run` انجام دهید تا پیش‌پردازش تصویر و خواندن دیسک، نخ UI را مسدود نکند. نمایش پیشرفت هر فایل را می‌توانید با فراخوانی آداپتور در سرویس میزبان یا توسعهٔ رویداد پیشرفت انجام دهید. در این نسخه گزارش پیشرفت درصدی وجود ندارد.

## قرارداد داده

`Study.Sources` متن و محل منبع را نگه می‌دارد. `Observations` فهرست کاندیداهاست، نه یک دیکشنری که مقدار قبلی را بی‌صدا بازنویسی کند. شناسهٔ `SourceId` هر کاندیدا به صفحه و `LineIndex` به خط منبع اشاره دارد. فایل تکراری بر اساس SHA-256 حذف می‌شود؛ دو عکس متفاوت از یک مقدار همچنان دو شاهد مستقل هستند.

`ReviewHistory` انتخاب یا اصلاح اپراتور را به‌ترتیب اضافه می‌کند؛ آخرین مورد هر کلید در خروجی استفاده می‌شود. میزبان مسئول اجازهٔ دسترسی و حفظ تمامیت این سابقه است. مقدار بررسی‌شده ممکن است دستی باشد و `ObservationId` نداشته باشد، اما نام بررسی‌کننده و دلیل الزامی‌اند.

## افزودن فیلد یا دستگاه

قواعد regex در `ExtractionProfile` روی هر خط استخراج‌شده اعمال می‌شوند و باید گروه نام‌دار `value` داشته باشند؛ گروه `unit` اختیاری است. زمان هر تطبیق محدود است. مثال:

```json
{
  "Key": "Custom.Measurement",
  "Pattern": "^CUSTOM\\s+(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>mm|cm)\\b",
  "RequiredUnit": "mm"
}
```

قواعد باید از تنظیمات مورد اعتماد برنامه بیایند و روی نمونه‌های دستگاه آزموده شوند. موتور، رابطهٔ چند سطر یا هویت رگ/جنین را از این regex استنباط نمی‌کند. برای دستگاه‌هایی که به تحلیل چیدمان، جدول یا مدل تصویری دیگری نیاز دارند، `IImageTextReader` را پیاده کنید. آداپتور ساختاریافتهٔ DICOM/SR در این نسخه آماده نیست و باید به مسیر ورود دادهٔ بررسی‌پذیر اضافه شود.

قالب، استخراج و برنامهٔ میزبان مستقل‌اند؛ افزودن قالب جدید معمولاً فقط DOCX و فایل نگاشت را تغییر می‌دهد. انتخاب خودکار نوع قالب و مرکز جزو مسئولیت نرم‌افزار میزبان است.
