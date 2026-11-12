# Localization

RecoverX is **English only**. UI text still lives in `src/RecoverX.Desktop/Resources/Strings.resx` rather than in code, which keeps wording consistent and reviewable.

- XAML uses `{loc:Loc Key}`; code uses `ILocalizer.Get/Format`. Core and Database return resource keys, never display text.
- A missing key shows as `[Key]` and is logged once.
- `python scripts/check-localization.py` verifies that every key used in XAML or code exists, including keys built dynamically (condition, outcome, reason and status names).
