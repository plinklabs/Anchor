# AQ1 — Cross-surface consistency & accessibility audit

The QA capstone of the Anchor visual-identity redesign epic (#180). It checks the
three surfaces — **dashboard (paper)**, **agent (ink)**, **extension (ink)** — against
the in-repo brand (`design/ANCHOR_BRAND.md`) and the design-system tokens, for both
brand consistency and accessibility.

- **Issue:** plinklabs/Anchor#179 (AQ1).
- **Depends on:** the AD/AA/AE screen work (#166–#178) and the foundations #162–#165 —
  all merged before this audit ran.
- **Scope of this document:** the findings. This is a **documented audit** — it records
  the state of each surface and the evidence behind each verdict. It is not a behavioural
  change; every defect it would have raised is instead tracked as a follow-up issue (see
  §6). At the time of writing, the surfaces are compliant and no follow-ups were required.

---

## 1. The 13 surfaces

| # | Surface | Treatment | Redesign issue | Screenshot / e2e vehicle |
|---|---|---|---|---|
| 1 | Dashboard — login | paper | #167 | `integration_test/login_test.dart` |
| 2 | Dashboard — home | paper | #168 | `integration_test/home_test.dart` |
| 3 | Dashboard — live session (instrument panel) | paper | #169 | `integration_test/live_session_test.dart` |
| 4 | Dashboard — bundles editor | paper | #170 | `integration_test/bundles_paper_test.dart` |
| 5 | Dashboard — classes/roster + add-student modal | paper | #171 | `integration_test/classes_paper_test.dart` |
| 6 | Dashboard — history list | paper | #172 | `integration_test/history_archive_test.dart` |
| 7 | Dashboard — past-session detail | paper | #172 | `integration_test/history_archive_test.dart` |
| 8 | Agent — MainWindow | ink | #173 | `IntegrationTests/MainWindowVisualTests.cs` |
| 9 | Agent — FocusOverlay | ink | #174 | `IntegrationTests/OverlayVisualTests.cs` |
| 10 | Agent — JoinByCode | ink | #175 | `IntegrationTests/JoinByCodeVisualTests.cs` |
| 11 | Agent — JoinConfirmation | ink | #175 | `IntegrationTests/JoinByCodeVisualTests.cs` |
| 12 | Extension — block page | ink | #177 | `e2e/specs/block-page-theme.spec.ts` |
| 13 | Extension — status popup | ink | #178 | `e2e/specs/popup-theme.spec.ts` |

Each surface already ships its own real-app visual/e2e test from its redesign PR; this
audit verifies against those existing vehicles rather than re-screenshotting by hand, so
the checks stay reproducible in CI.

---

## 2. Method

For each surface the audit confirmed, from the source and the existing e2e tests:

1. **Palette** — colours come from DS tokens (CSS custom properties / `PlinkTheme` /
   `PlinkResources.xaml` brushes), no ad-hoc hex except the two documented exceptions
   (§5). Magenta stays the single spark; the indigo accent is reserved to the mark /
   identity rule.
2. **Type** — Fraunces (display), Hanken Grotesk (body), Space Mono (eyebrows/specs),
   referenced through DS type tokens, never a raw system font stack.
3. **Hairlines not shadows** — borders use `--border` / the DS hairline; no `box-shadow`
   elevation as structure.
4. **Ping motif** — the signature concentric-ring ping marks liveness; static where the
   surface is not itself the live signal.
5. **Ink (student) vs paper (teacher)** — fixed per surface, never system-following.
6. **Contrast + focus rings** — text/graphics clear WCAG AA on the surface's one fixed
   background; focus rings are the on-surface magenta (`#DB2777` paper, `#EC4899` ink).
7. **Reduced motion** — the ping pulse is gated behind
   `prefers-reduced-motion: no-preference`; the agent ping defers to the OS animations
   setting through the DS control.

---

## 3. Findings by surface family

### Dashboard (paper) — PASS

- Built on `PlinkTheme.paper`, with **no** `ThemeMode.system` and no dark `ThemeData`
  (`dashboard/lib/main.dart`). The single Anchor-specific layer is the
  `PlinkProductAccent(Color(0xFF34357A))` theme extension — the deep-indigo accent on its
  on-paper value, reserved to the mark/identity. Magenta is untouched.
- The paper e2e tests assert the **single magenta spark per page** (e.g. Save is the only
  spark in bundles/classes) and **no `RenderFlex` overflow at the real font** — the
  contrast/target/reflow guarantees of §7 of the brand, checked in the real app with real
  fonts and real layout.
- Verdict: consistent with the paper treatment; accessibility guarantees covered by the
  existing integration suite.

### Agent (ink) — PASS

- `App.xaml` pins `RequestedTheme="Dark"` so the agent never follows the OS, and every
  window backs itself with `{StaticResource PlinkSurfaceInkBrush}` (MainWindow,
  FocusOverlay, JoinByCode, JoinConfirmation) — not the system
  `{ThemeResource SolidBackgroundFillColorBaseBrush}` that would flip with the OS. A
  startup self-test (`App.xaml.cs`) asserts the pin so dropping it fails loudly rather
  than silently shipping a light agent.
- The per-product accent is set on its **on-ink** value (`PlinkProductAccentColor`
  `#FF7E80D2`) after the DS merge, reserved to the mark.
- The ping is the DS `pds:Ping` control: static where the window is not itself the live
  signal (the overlay), live where it is (the MainWindow connection/heartbeat). Animation
  defers to the OS animations-enabled setting through the DS control, satisfying the
  reduced-motion rule for WinUI.
- Visual e2e tests (`MainWindowVisualTests`, `OverlayVisualTests`, `JoinByCodeVisualTests`)
  drive the **real window** and PrintWindow-screenshot it; `BrandAssetsTests` covers the
  shipped brand assets.
- Verdict: consistent with the ink treatment; the OS-swap hard rule is enforced in code
  and self-tested.

### Extension (ink) — PASS

- Both `block-page.html` and `popup.html` hard-wear `class="plink-ink"` on `<body>` and
  pin `color-scheme: dark` for native controls/scrollbars — with **no**
  `@media (prefers-color-scheme: …)` swap. Guard tests (`block-page.test.ts`,
  `popup.test.ts`) assert the absence of that media swap, so a regression fails CI.
- Both surfaces compose from `.pl-*` classes and DS tokens; the only Anchor-specific token
  is `--product-accent: #7e80d2` (on-ink indigo) on the identity rule. The ping uses
  `.pl-ping--pulse .pl-ping--on-ink` (brighter on-ink magenta), and the DS already freezes
  the pulse under `prefers-reduced-motion` (`plink.css` gates `@keyframes` behind
  `prefers-reduced-motion: no-preference`). The popup correctly swaps to the **static**
  ping in its idle state.
- Buttons are DS `.pl-btn`, whose `:focus-visible` is the 2px/2px on-ink magenta ring; the
  `.plink-ink` block remaps `--focus-ring` to `#EC4899`, so on-ink controls get the
  brighter ring for free.
- Theme e2e specs (`block-page-theme.spec.ts`, `popup-theme.spec.ts`) load the real
  unpacked extension under Playwright.
- Verdict: consistent with the ink treatment; the no-OS-swap hard rule is guarded by tests.

---

## 4. Cross-cutting checks

| Check (brand §) | Dashboard | Agent | Extension |
|---|---|---|---|
| Palette from DS tokens, magenta = single spark | PASS | PASS | PASS |
| Indigo accent reserved to the mark/identity | `#34357A` | `#7E80D2` | `#7E80D2` |
| Fraunces / Hanken / Space Mono via DS type tokens | PASS | PASS | PASS |
| Hairlines, not shadows | PASS | PASS | PASS |
| Ping motif (live vs static) | PASS | PASS | PASS |
| Ink/paper fixed, never system-following (§6) | paper, no `ThemeMode.system` | `RequestedTheme="Dark"` + self-test | `.plink-ink` + no media swap, test-guarded |
| Contrast AA on the one fixed background (§7) | PASS | PASS | PASS |
| Focus ring = on-surface magenta, 2px/2px (§7) | `#DB2777` | DS control | `#EC4899` (remapped) |
| `prefers-reduced-motion` / OS animations (§8) | PASS | OS animations setting | DS-gated pulse |
| Calm, plain voice (§5) | PASS | PASS | PASS |

---

## 5. Documented exceptions (intentional, not defects)

Two non-token colours exist on the ink surfaces. Both are deliberate and documented in
place; neither is a brand violation:

1. **Error red `#f87171`** on the block-page status line — the system palette has no error
   token; this is a legible on-ink red, used only for the transient request-failure
   message, never as a structural colour.
2. **Idle/static ping** in the popup — a state choice (no active session), not a colour
   change.

No new error token is proposed here; if the DS later adds one, these should adopt it. That
is a design-system concern, not an Anchor defect — see §6.

---

## 6. Follow-up issues

The audit found **no behavioural defects** on any of the 13 surfaces — the AD/AA/AE
redesign work and the AF foundations together left every surface consistent with the brand
and meeting the accessibility floor, with the two hard rules (no OS theme swap) enforced in
code and guarded by tests.

One **upstream, non-blocking** observation, recorded for completeness rather than filed
against Anchor: the design system has no semantic **error/danger token**, so the block page
carries a local `#f87171`. If that recurs across products it belongs in the DS palette, not
in per-product CSS. This is a `plink-design-system` enhancement, not an Anchor surface
defect, and does not gate this epic.

If a future regression is found on any surface, file it against the owning screen issue's
area and reference this audit.

---

## 7. Verdict

**PASS.** All 13 surfaces are consistent with `design/ANCHOR_BRAND.md` and the DS tokens,
and meet the accessibility floor (contrast, focus rings, targets, reduced motion). The
ink/paper split is fixed per surface and enforced in code on both student surfaces. The
visual-identity redesign epic (#180) is, on the evidence of the shipped code and its
real-app test suites, complete.
