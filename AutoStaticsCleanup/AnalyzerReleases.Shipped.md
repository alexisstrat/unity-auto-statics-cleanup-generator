## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ASC001  | AutoStaticsCleanup | Error | Type and every enclosing type must be 'partial'
ASC002  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] on a readonly field that can't be reset via Clear()
ASC003  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] on a property without a settable setter
ASC004  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] on a manual (non-field-like) event
ASC006  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] on an instance member
ASC007  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] on a const field
ASC008  | AutoStaticsCleanup | Error | [AutoStaticsCleanup] is incompatible with an explicit static constructor on the same type
