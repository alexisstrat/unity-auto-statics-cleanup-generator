## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ASC001  | AutoStaticsCleanup | Error   | Type and every enclosing type must be 'partial'
ASC002  | AutoStaticsCleanup | Warning | [AutoStaticsCleanup] on a readonly field will be ignored
ASC003  | AutoStaticsCleanup | Warning | [AutoStaticsCleanup] on a property without a settable setter will be ignored
ASC004  | AutoStaticsCleanup | Warning | [AutoStaticsCleanup] on a manual (non-field-like) event will be ignored
ASC006  | AutoStaticsCleanup | Warning | [AutoStaticsCleanup] on an instance member will be ignored
ASC007  | AutoStaticsCleanup | Warning | [AutoStaticsCleanup] on a const field has no effect
