# AutoReport

کتابخانهٔ C# برای **.NET Framework 4.8** جهت استخراج داده‌های نوشته‌شده روی تصاویر گزارش سونوگرافی، نگهداری داده و شواهد در JSON، و تکمیل قالب Word هر مرکز. پردازش در رایانهٔ مرکز و روی CPU انجام می‌شود؛ برنامه هیچ درخواست شبکه‌ای ندارد.

این نسخه یک پایهٔ قابل اتصال و قابل توسعه است، نه محصول اعتبارسنجی‌شده برای همهٔ دستگاه‌ها و همهٔ مراکز. آزمون صحت بالینی، اندازه‌گیری دقت و تست عملکرد در محیط واقعی هنوز لازم است. نمونه‌های بیمار و خروجی استخراج آن‌ها در این مخزن قرار ندارند.

## قابلیت‌های پیاده‌شده

- کتابخانهٔ `AutoReport.Core.dll` با هدف `net48` و `AnyCPU`، بدون وابستگی NuGet در زمان اجرا.
- آداپتور آفلاین Tesseract با خروجی TSV، محدودیت زمان، لغو عملیات، تعداد نخ قابل تنظیم و گذرهای تصویر اصلی، خاکستری و متن زرد. گذر اصلی و متن زرد قابل غیرفعال‌سازی‌اند؛ گذر متن زرد فقط در صورت وجود پیکسل‌های زرد اجرا می‌شود.
- ذخیرهٔ تمام خطوط خوانده‌شده، مختصات در تصویر اصلی، شناسه و SHA-256 منبع، کاندیداهای مقدار و واحد، و سوابق بررسی.
- قواعد استخراج JSON قابل تنظیم؛ فیلدهای دلخواه بدون تغییر کلاس‌های اصلی. قواعد اولیه برای بخشی از اندازه‌گیری‌های مامایی هستند.
- نگاشت جداگانهٔ هر مرکز از جای‌خالی Word به کلید داده؛ پشتیبانی از `{{Token}}` و Content Control با Tag برابر `AutoReport:Token`.
- تکمیل DOCX در متن، جدول، سرصفحه، پاصفحه، متن‌باکس و یادداشت‌ها، همراه با پشتیبانی از شکسته‌شدن جای‌خالی بین چند run ورد.
- حفظ مقادیر متعارض به‌صورت کاندیدا؛ تبدیل صریح mm/cm و g/kg؛ تفکیک HC و HC*، همچنین GA/EDD بر اساس LMP و AUA.
- خروجی پیش‌نویس با نشان واضح؛ خروجی نهایی مستلزم بررسی همهٔ فیلدهای استفاده‌شده و تأیید کل متن گزارش است.
- برنامهٔ خط فرمان برای آزمایش و نمونهٔ اتصال به نرم‌افزار میزبان.

## پیش‌نیاز و آفلاین بودن

کامپیوتر مقصد به Windows دارای .NET Framework 4.8 و یک نسخهٔ سازگار محلی Tesseract 5، DLLهای همراه آن و مدل زبان نیاز دارد. کتابخانهٔ C# به GPU و AVX وابسته نیست، اما سازگاری CPU/Windows موتور بومی به **نسخهٔ باینری انتخاب‌شده** وابسته است. برچسب AnyCPU به معنی اجرای تضمینی روی هر پردازنده یا هر سیستم‌عامل نیست. نسخه‌های x86 و x64 مورد نیاز مراکز باید جداگانه روی سخت‌افزار هدف آزموده شوند.

موتور بومی و مدل در مخزن باندل نشده‌اند. آن‌ها را یک‌بار در مرحلهٔ آماده‌سازی نصب تهیه کرده و همراه نصب‌کنندهٔ خود توزیع کنید؛ برنامه هنگام اجرا هیچ دانلودی انجام نمی‌دهد. مدل `eng.traineddata` برای نمونه‌های فعلی لازم است. برای متن فارسی، `fas.traineddata` را اضافه کرده و `Languages` را `eng+fas` تنظیم کنید. بستهٔ نصب باید مجوزهای موتور، مدل‌ها و کتابخانه‌های همراه را حفظ کند.

Tesseract تحت Apache-2.0 منتشر می‌شود. مستندات اصلی: [مخزن موتور](https://github.com/tesseract-ocr/tesseract)، [فرمان TSV](https://tesseract-ocr.github.io/tessdoc/Command-Line-Usage.html)، [مدل‌های سریع](https://github.com/tesseract-ocr/tessdata_fast). هیچ سرویس پولی در این پیاده‌سازی استفاده نمی‌شود؛ نوع مدل مناسب و سرعت واقعی باید روی سخت‌افزار مرکز ارزیابی شوند.

## ساخت روی Windows

Visual Studio 2022 Build Tools با .NET Framework 4.8 targeting pack و ابزارهای .NET SDK نصب باشد. از Developer Command Prompt در ریشهٔ مخزن:

```bat
msbuild src\AutoReport.Cli\AutoReport.Cli.csproj /restore /p:Configuration=Release
msbuild tests\AutoReport.Tests\AutoReport.Tests.csproj /restore /p:Configuration=Release
tests\AutoReport.Tests\bin\Release\net48\AutoReport.Tests.exe config\extraction.default.json
```

فایل‌های کتابخانه و CLI در `src/AutoReport.Cli/bin/Release/net48` تولید می‌شوند. گردش‌کار GitHub Actions همین ساخت و آزمون را روی Windows اجرا می‌کند و فایل‌ها را در artifact قرار می‌دهد. اتصال شبکه در مرحلهٔ توسعه/تهیهٔ وابستگی‌ها با اجرای آفلاین در مرکز متفاوت است.

## آزمایش جریان کامل

مسیر موتور و مدل‌ها را در یک کپی از `config/engine.example.json` تنظیم کنید. خروجی‌ها را در پوشهٔ محلی خارج از مخزن نگه دارید. هر پوشه باید فقط تصاویر **یک نوبت معاینهٔ یک بیمار** را داشته باشد؛ پوشهٔ مادر ZIP ارسالی ورودی یک معاینه نیست.

```bat
AutoReport.Cli.exe extract exam-001 C:\Exams\001 config\extraction.default.json config\engine.example.json C:\Reports\study.json --single-study
AutoReport.Cli.exe sample-template C:\Reports\example.docx --example
AutoReport.Cli.exe inspect C:\Reports\example.docx
AutoReport.Cli.exe draft C:\Reports\example.docx config\template.example.json C:\Reports\study.json C:\Reports\draft.docx
```

نمونهٔ Word ساخته‌شده فقط قالب فنی آزمایش است و متن جواب نرمال یک مرکز نیست. موتور نام بیمار را از نام فایل به‌عنوان هویت معتبر برداشت نمی‌کند؛ میزبان باید شناسهٔ معاینه و تعلق تصاویر به همان بیمار را کنترل کند.

برای تأیید یک کاندیدا، شناسهٔ آن را از JSON بردارید و پس از تطبیق با تصویر اجرا کنید:

```bat
AutoReport.Cli.exe approve C:\Reports\study.json observation-id reviewer-name C:\Reports\reviewed.json --verified
```

برای اصلاح دستی یا تأیید چند فیلد در رابط نرم‌افزار خود، APIهای `SetReviewed` و `Approve` را استفاده کنید. خروجی جدید بنویسید تا نسخهٔ قبلی حفظ شود. پس از بررسی همهٔ فیلدها و **تمام جملات ثابت نرمال**:

```bat
AutoReport.Cli.exe final C:\Reports\example.docx config\template.example.json C:\Reports\reviewed.json C:\Reports\final.docx reviewer-name --whole-report-reviewed
```

## تعریف قالب هر مرکز

در Word، جای مقدار را با `{{BPD}}` یا یک Content Control متنی با Tag برابر `AutoReport:BPD` علامت بزنید. یک نگاشت نمونه:

```json
{
  "CenterId": "center-001",
  "TemplateVersion": "1",
  "Bindings": [
    { "Token": "BPD", "Key": "Obstetric.BPD", "OutputUnit": "mm", "IncludeUnit": true }
  ]
}
```

نام توکن می‌تواند فارسی باشد. برای فیلدهایی که واحد کنار جای‌خالی نوشته شده، `IncludeUnit` را `false` قرار دهید. جای‌خالی‌های تکراری با یک نگاشت پر می‌شوند. قالب و نگاشت را به‌صورت نسخه‌بندی‌شده در نرم‌افزار میزبان به مرکز متصل کنید. قالب آزادِ بدون جای‌خالی به یک‌بار علامت‌گذاری نیاز دارد؛ فهم خودکار منظور هر خط‌چین یا جای سفید پیاده نشده است.

## محدودیت‌های مشخص این نسخه

- ورودی آداپتور فعلی: JPG/JPEG، PNG و BMP. DICOM/SR، PDF، ویدئو و TIFF چندصفحه‌ای هنوز آداپتور ندارند.
- خواندن مستقل همهٔ مدل‌های دستگاه تضمین نمی‌شود. چیدمان جدید، جدول چندستونه یا متن کوچک ممکن است نیازمند قواعد/آداپتور جدید باشد؛ متن خام برای این موارد حفظ می‌شود.
- موتور عددی را که در تصویر موجود نیست محاسبه یا تولید نمی‌کند؛ از خود آناتومی تصویر، تشخیص یا نرمال‌بودن استخراج نمی‌کند.
- `D1` و `D2` به اندام خاص نسبت داده نمی‌شوند. مقادیر داپلر بدون انتساب مطمئن به رگ با کلید `Doppler.Unspecified.*` ذخیره می‌شوند. تفکیک چندجنینی خودکار فعلاً پیاده نشده است.
- OCR confidence یک امتیاز موتور است، نه احتمال صحت پزشکی. حتی امتیاز بالا تأیید خودکار ایجاد نمی‌کند.
- یک واحد ناخوانا مانند g که به 9 تبدیل شود، از روی حدس اصلاح نمی‌شود. فیلد گمشده یا متعارض باید بررسی شود.
- فقط DOCX پشتیبانی می‌شود؛ DOC قدیمی باید در Word به DOCX تبدیل شود. جای‌خالی نباید از یک پاراگراف به پاراگراف دیگر ادامه پیدا کند. Content Control تودرتو، چندپاراگرافی و جدولی برای مقدارگذاری پشتیبانی نمی‌شود.
- Track Changes باید پیش از استفاده در قالب تعیین تکلیف شود. جای‌خالی در کد فیلد Word و عناصر رسم غیرمتنی پشتیبانی نمی‌شود. موتور، Word renderer یا PDF exporter نیست؛ چیدمان قالب واقعی باید در Word بررسی شود.
- هر فراخوانی یک معاینه است. احراز هویت اپراتور، ذخیرهٔ رمزگذاری‌شده، سیاست نگهداری داده و جلوگیری از تغییر سوابق در نرم‌افزار میزبان قرار می‌گیرد؛ JSON امضای ضدتغییر ندارد.
- این مخزن شامل رابط گرافیکی تولیدی، نصب‌کنندهٔ موتور بومی و داشبورد هزار مرکز نیست. هسته برای اتصال به محصول موجود ارائه شده است.

[نمونهٔ اتصال C#](docs/integration.md) و [روش اعتبارسنجی](docs/validation.md) را ببینید.


## Offline ultrasound key/value extraction

The WinForms application runs PaddleOCR locally; no cloud API is required. OCR blocks retain bounding boxes and confidence, then the core pipeline reconstructs rows, applies deterministic extraction rules, adds explicit Doppler side/vessel context when it is present in the image text, and emits `Study.KeyValues`.

Example:

```json
"KeyValues": {
  "Doppler.LeftUterineArtery.PI": {
    "Value": "0.88",
    "Unit": "",
    "Confidence": 96.4,
    "Evidence": "PI 0.88",
    "Warnings": []
  }
}
```

Conflicting OCR candidates are not averaged. The highest-confidence candidate is retained and marked with `ConflictingCandidates` so medical measurements can be reviewed against the source image.
