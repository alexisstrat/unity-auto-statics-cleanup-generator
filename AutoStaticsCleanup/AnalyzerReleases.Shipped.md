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

## Release 1.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ASC009  | AutoStaticsCleanup | Error | Disposable static without an initializer cannot be reset
ASC010  | AutoStaticsCleanup | Error | Readonly member is null at cleanup time; the generated Clear() would throw
