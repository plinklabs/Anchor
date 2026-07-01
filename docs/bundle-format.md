# Bundle JSON format

Anchor bundles are allow-lists of domains and apps that a session permits.
Admins can **export** bundles to JSON and **import** JSON back from the Bundles
admin page (Admin → Bundles). This document describes the JSON shape so a person
— or an LLM — can author a valid file by hand.

## Worked example

A single bundle is a plain object with a `name` and a list of `entries`:

```json
{
  "name": "Example bundle",
  "entries": [
    { "kind": "Domain", "value": "example.com",       "matchType": "Exact" },
    { "kind": "Domain", "value": "*.khanacademy.org",  "matchType": "Wildcard" },
    { "kind": "Domain", "value": "geogebra.org",       "matchType": "Suffix" },
    { "kind": "App",    "value": "msedge",             "matchType": "Exact" },
    { "kind": "App",    "value": "Microsoft.Word",     "matchType": "SignedPublisher" }
  ]
}
```

## Fields

### Bundle

| Field     | Type     | Required | Notes                                              |
| --------- | -------- | -------- | -------------------------------------------------- |
| `name`    | string   | yes      | 1–128 characters. Unique within the catalogue.     |
| `entries` | array    | yes      | At least one entry.                                |

Server-managed fields — `id`, `version`, `isArchived`, `hasBeenUsed` — are
**ignored on import** and you do not need to provide them. Exported files omit
them too.

### Entry

| Field       | Type   | Required | Notes                                            |
| ----------- | ------ | -------- | ------------------------------------------------ |
| `kind`      | enum   | yes      | `Domain` or `App`.                               |
| `value`     | string | yes      | 1–512 characters. See per-kind rules below.      |
| `matchType` | enum   | yes      | See the allowed combinations below.              |

Enum values are matched **case-insensitively** on import (`domain`, `Domain`,
and `DOMAIN` all work), but exports use the canonical capitalisation shown here.

#### `kind: "Domain"`

`value` is a hostname. Allowed `matchType`:

| `matchType` | Meaning                                | Example `value`      |
| ----------- | -------------------------------------- | -------------------- |
| `Exact`     | Matches that host only.                | `example.com`        |
| `Wildcard`  | A leading `*.` matches any subdomain.  | `*.khanacademy.org`  |
| `Suffix`    | Matches the host and any subdomain.    | `geogebra.org`       |

`SignedPublisher` is **not** valid for a domain. The hostname must look like a
domain (letters/digits/hyphen labels separated by dots, with an optional leading
`*.` for `Wildcard`).

#### `kind: "App"`

`value` is a Windows process name or a signed-publisher identity. Allowed
`matchType`:

| `matchType`       | Meaning                                         | Example `value`    |
| ----------------- | ----------------------------------------------- | ------------------ |
| `Exact`           | Matches that process name.                      | `msedge`           |
| `SignedPublisher` | Matches apps signed by that publisher identity. | `Microsoft.Word`   |

`Wildcard` and `Suffix` are **not** valid for an app. For `Exact`, the process
name must be the bare name — **no path** (`\` or `/`) and **no `.exe` suffix**
(write `msedge`, not `C:\…\msedge.exe`).

## Export

- **Export** (on a selected bundle) downloads that one bundle in the bare-object
  form shown above, named `<bundle-name>.json`.
- **Export all** downloads every bundle in the current view wrapped in an
  envelope, named `bundles.json`:

  ```json
  {
    "schemaVersion": 1,
    "bundles": [
      { "name": "Example bundle", "entries": [ /* … */ ] }
    ]
  }
  ```

## Import

Import accepts any of three shapes, so both an exported single bundle and an
exported "all" file load without editing:

1. the **envelope** `{ "schemaVersion": 1, "bundles": [ … ] }`,
2. a **bare single bundle** `{ "name": …, "entries": [ … ] }`, or
3. a **bare array** `[ { "name": …, "entries": [ … ] }, … ]`.

`schemaVersion` is optional on import and currently informational only.

### Create vs. update (upsert by name)

Each imported bundle is matched against the catalogue **by name**
(case-insensitive):

- a **new** name **creates** a bundle;
- an **existing** name **updates** that bundle in place (this bumps its version,
  and un-archives it if it was archived).

So a round-trip (export → import) reproduces an equivalent bundle, and importing
the same file twice updates rather than duplicates.

### Validation

The whole file is validated **before** anything is written. If any bundle or
entry is invalid — bad JSON, a missing field, an unknown enum value, a disallowed
`kind`/`matchType` combination, or two bundles sharing a name — the import is
**rejected** with a per-error list and nothing is created or updated. Fix the
file and import again.
