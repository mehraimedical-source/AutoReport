# Dynamic device/profile architecture

AutoReport should not hard-code a customer or an ultrasound model.

Pipeline: DICOM metadata/OCR -> device hint -> measurement-group classification -> generic label/value extraction -> canonical normalization -> confidence/review -> customer template mapping.

Device-specific configuration is preferred; device-specific code is only an escape hatch. Unknown devices use the generic path.

Each extracted value keeps its source image and confidence. Low-confidence values remain unresolved instead of being guessed.

Customer templates use canonical placeholders such as `{{afi}}`, `{{q4}}`, `{{pi}}`. Production mappings should additionally carry measurement-group and laterality context (for example right uterine PI vs MCA PI) to prevent collisions.

This system transfers measurements displayed by the ultrasound system; it does not perform diagnosis.
