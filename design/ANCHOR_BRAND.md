# Anchor — brand

The in-repo source of truth for **Anchor's** identity: its name, its one reserved
accent colour, its mark and lockup, and its voice. Anchor is the first Plink Labs
product to use the **per-product accent** convention (plink-design-system#5 / DS-5).

> Anchor does **not** redefine the Plink Labs foundations. It inherits all of them —
> paper + ink, the magenta spark, the Fraunces / Hanken Grotesk / Space Mono type,
> hairlines (never shadows), the ~6px radius, and the ping motif. Anchor adds exactly
> two things on top: **one reserved accent colour** and **its own mark/lockup**.
> Everything else comes from the design system unchanged.

Foundations live upstream: `github.com/plinklabs/plink-design-system` (`readme.md`,
`tokens/`, `guidelines/`). When this doc and the foundations disagree, the foundations win
for anything shared; this doc only governs what is Anchor-specific.

---

## 1. Naming — `Anchor` vs `FocusAgent`

There is **one user-facing name: Anchor.** `FocusAgent` is an internal engineering name only.

| Context | Use | Notes |
|---|---|---|
| Product name in UI, window titles, store listing, marketing, docs, README headings | **Anchor** | Capitalised proper noun in prose ("Open Anchor", "Anchor is connected"). |
| The mark / lockup wordmark lettering | **anchor** (lowercase) | Matches the lowercase "plink labs" wordmark convention. The *logo* is lowercase; *prose* is capitalised. |
| Always paired as | **Anchor**, a Plink Labs instrument | Anchor is a product *within* Plink Labs — never a standalone brand. |
| Code: C# namespace, project (`FocusAgent.App`), assembly | **FocusAgent** | Internal. Do not rename as part of branding work — it is not user-visible. |
| Package identity / MSIX (`net.arcadia.anchor.focusagent`), log paths (`…\Anchor\FocusAgent\logs`) | both, as-is | These already encode "Anchor" as the product and "FocusAgent" as the component. Leave them. |

**Rule of thumb:** if a human who is not on the engineering team can read it, it says
**Anchor**. If only a developer or a log parser reads it, `FocusAgent` is fine.

---

## 2. The Anchor accent — deep indigo

Anchor's reserved accent is a **deep indigo**. It carries the brand in *branding contexts only* —
the mark, the lockup, the app/tray/splash icons, a store hero. It is **not** an in-app spark and
**not** a UI action colour.

| Token | Light (on paper) | On ink | Use |
|---|---|---|---|
| `--anchor-accent` | `#34357A` | — | The mark/lockup on paper surfaces (the teacher dashboard). |
| `--anchor-accent-ink` | — | `#7E80D2` | The mark/lockup on ink surfaces (student agent + extension). Brightened to clear AA on `#1B1B23`. |
| `--anchor-accent-press` | `#2A2B63` | `#8486D6` | Reserved darken/lighten for any rare interactive branding element (e.g. a brand splash button). |

### Why indigo, and the hard boundary with magenta

Magenta `#DB2777` (`#EC4899` on ink) remains the **single Plink spark** — the mark's highlight,
one word per screen, the pulse, one primary action, **<5% of pixels**. That rule is unchanged inside
Anchor. The Anchor accent exists to give Anchor an identity *without touching the spark*:

- **Different job, different place.** Magenta = the live, attention-grabbing spark *inside* the app.
  Indigo = the calm, standing identity of the product *around* the app (icon, lockup, splash).
  They rarely share a surface; when they do (e.g. a splash with the lockup and a magenta CTA), the
  indigo recedes and the magenta is still the one bright point.
- **Indigo recedes.** It is cool, low-energy, and close to ink — it reads as depth and steadiness
  (an anchor), never as a call to action. It will not be mistaken for the spark and will not compete
  with it for the eye.
- **Never repaint with it.** Indigo is not a fill colour for cards, headers, or large areas any more
  than magenta is. Same discipline: a spark/accent, never a coat of paint. Flat fills only — no
  gradients, no glass.

### Contrast

- `#34357A` on paper `#FAF7F2` ≈ **10.2:1** — AAA; comfortable for the mark and any wordmark use.
- `#7E80D2` on ink `#1B1B23` ≈ **4.8:1** — clears AA (4.5:1), well past the 3:1 graphical-object floor;
  correct for the mark on ink. Do not use `#34357A` directly on ink (≈1.6:1, far too dark) — switch
  to `#7E80D2`.

### Wiring it through the bindings (DS-5 slot)

DS-5 defines the per-product accent as an *override point*, not a foundation edit. Anchor sets the
slot in each binding when AF3 (#164) wires the platform bindings — it does **not** edit the upstream
token files:

- **CSS / extension** — set the Anchor accent variables on `:root` *after* importing the DS
  `styles.css`, e.g. `--anchor-accent: #34357A;` (and `--anchor-accent-ink` on ink surfaces).
- **Flutter / dashboard** — expose the accent on the Anchor theme extension layered over the DS
  `ThemeData` (paper context → `#34357A`).
- **WinUI / agent** — add the indigo brushes (`AnchorAccent`, `AnchorAccentInk`) to the app
  `ResourceDictionary` layered over the DS XAML dictionary; ink surfaces use `AnchorAccentInk`.

---

## 3. The Anchor mark

**Concept — anchor-from-the-ping.** The Plink ping (open ring + filled centre) *becomes* the
anchor's shackle: the open ring is the shackle, with the ping's filled centre as the shackle pin.
A hairline stem drops through a short stock (crossbar) to a calm fluke arc. One device does double
duty — it is unmistakably an anchor *and* unmistakably a Plink ring.

Drawn in the accent indigo, in the hairline language of the system (even stroke weight, round caps,
no fills except the centre pin). No shadow, no gradient, no enclosing badge.

### Assets

| File | Surface | Stroke |
|---|---|---|
| [`anchor-mark.svg`](anchor-mark.svg) | paper / light (dashboard) | `#34357A` |
| [`anchor-mark-dark.svg`](anchor-mark-dark.svg) | ink / dark (agent, extension) | `#7E80D2` |

Both are 56×56 viewBox, transparent background, 2.5px stroke. Always pick the variant that matches
the surface it sits on (paper → light, ink → dark).

### Usage

- **Clear space:** keep at least the height of the shackle ring (~¼ of the mark) clear on all sides.
- **Minimum size:** 16px (favicon/tray floor). Below ~20px the fluke tips and centre pin start to
  fill in — that is acceptable down to 16; do not go smaller.
- **Colour:** indigo accent only (per surface). Never magenta, never multi-colour, never filled solid.
- **Don't:** rotate it, add a drop shadow, put it in a coloured circle/box, stretch it, or recolour
  the wordmark/mark to compete with the spark.

> The icon/tray/splash/favicon *production* renders (PNG/ICO at each size, the tray monochrome
> treatment) are **AF2 (#163)**. This issue defines the mark; AF2 ships the asset set built from it.

---

## 4. The lockup

Mark + wordmark, horizontally locked. The wordmark is **anchor** (lowercase), set in **Fraunces**
(the brand display face), weight ~560, tracking ≈ −0.018em — the same treatment as the "plink labs"
wordmark. The mark carries the indigo accent; the **wordmark stays ink/paper**, never indigo —
keeping the accent reserved to the mark.

| File | Surface | Mark | Wordmark |
|---|---|---|---|
| [`anchor-lockup-light.svg`](anchor-lockup-light.svg) | paper / light | `#34357A` | ink `#1B1B23` |
| [`anchor-lockup-dark.svg`](anchor-lockup-dark.svg) | ink / dark | `#7E80D2` | paper `#FAF7F2` |

- **Spacing:** the mark and wordmark sit on a shared 56px height; the wordmark starts at ~¼-mark of
  clear space to the right of the mark. Don't re-space them ad hoc — use the lockup file.
- **Stacking:** the horizontal lockup is the default. If a vertical (mark-over-word) lockup is ever
  needed, derive it from these files and add it here — don't improvise one inline.
- **Fonts:** the SVG references Fraunces by name (as the DS lockup does). Render where Fraunces is
  available, or outline the text before exporting to a fixed asset.
- **Don't:** set the wordmark in any other face, capitalise it, colour it indigo, or place the lockup
  without enough clear space.

---

## 5. Voice

Anchor inherits the Plink Labs voice — **plain, warm, a little dry; a teacher who respects your time,
not a growth marketer. Confidence from restraint, not exclamation marks.** Sentence case in prose and
UI; UPPERCASE wide-tracked Space Mono for eyebrows/specs; no emoji; concrete, verifiable claims.

Anchor-specific notes, because Anchor talks to a **student, mid-session**:

- **Calm, never nagging.** Anchor holds a student steady; it does not scold or gamify. "Anchor is
  keeping this session focused" — not "⚠️ You left your work!" or "Great job staying on task! 🎉".
- **State plainly, then stop.** Say what is happening and why in one short line, and get out of the
  way. "Your teacher started a focus session." / "Back to your tabs — the session ended."
- **Honest about control.** Be straight that a teacher set this up and that it ends. Never pretend
  Anchor is the student's own choice, and never imply surveillance for its own sake.
- **Dry, not cute.** A little understatement is welcome; mascots, cheerleading, and streaks are not.
- **Teacher-facing (dashboard) vs student-facing (agent/extension):** to the teacher Anchor is an
  instrument they operate ("Start a session", "3 students connected"); to the student Anchor is a
  quiet presence that explains itself. Same voice, different stance.

---

## 6. Surfaces — ink (student) vs paper (teacher)

Anchor inherits the system's paper + ink, but it does **not** let either surface
follow the operating system. Each surface wears **one fixed treatment, for its whole
life, on every machine** — chosen for *who* sits in front of it, not for an OS setting:

| Surface | Audience | Treatment | Base |
|---|---|---|---|
| Agent — every window (MainWindow, FocusOverlay, JoinByCode, JoinConfirmation) | student, mid-session | **ink** | ink `#1B1B23`, on-ink `#FAF7F2` |
| Extension — block page (and any future branded popup) | student, mid-session | **ink** | ink `#1B1B23`, on-ink `#FAF7F2` |
| Dashboard — every screen | teacher | **paper** | paper `#FAF7F2`, ink `#1B1B23` |

**Why fixed, and why this split.** The two student surfaces are **ink because ink
distracts a working student less** — a dark, quiet field recedes while they get back
to the task. The teacher dashboard is **paper because teachers skew older and broadly
dislike dark UI**; a calm, legible light instrument reads as trustworthy to them. These
are product decisions about the audience, so a student on a light-themed laptop still
gets ink and a teacher on a dark-themed one still gets paper. There is **no theme
toggle and no system-following** anywhere in Anchor.

**This is a hard rule, enforced in code — do not reintroduce a system swap:**

- **Extension / CSS** — the block page hard-wears `class="plink-ink"` on `<body>` and
  pins `color-scheme: dark` so native controls/scrollbars match. **Never** add a
  `@media (prefers-color-scheme: …)` swap or a `.plink-ink` ↔ paper toggle; the
  treatment is a class on the element, not a media query.
- **Agent / WinUI** — `App.xaml` sets `RequestedTheme="Dark"` so the app never follows
  the OS, and window backgrounds use the DS ink brush (`{StaticResource
  PlinkSurfaceInkBrush}`), not the system `{ThemeResource SolidBackgroundFillColorBaseBrush}`
  (which flips with the OS). **Never** drop `RequestedTheme` or reintroduce an
  OS-following `{ThemeResource}` for a surface colour.
- **Dashboard / Flutter** — build on the DS `PlinkTheme.paper`; do **not** wire
  `ThemeMode.system` or a dark `ThemeData`.

The magenta spark and the Anchor indigo are unchanged by the split — they keep their
**on-ink** values (`#EC4899` / `#7E80D2`) on the two ink surfaces and their **on-paper**
values (`#DB2777` / `#34357A`) on the dashboard, per §2.

---

## 7. Accessibility — contrast, focus, targets

Anchor follows the Plink Labs accessibility floor; the per-surface split (§6) just means
each surface is checked against **one** background, never both.

**Contrast.** Text and meaningful graphics clear **WCAG AA** (4.5:1 body, 3:1 large
text / graphical objects) on their fixed surface:

- On **paper `#FAF7F2`**: ink body `#1B1B23` ≈ 16:1; muted `#6E6A62` ≈ 4.6:1 (body-legal
  — don't go lighter for body); magenta `#DB2777` ≈ 4.5:1; indigo `#34357A` ≈ 10.2:1.
- On **ink `#1B1B23`**: on-ink `#FAF7F2` ≈ 15:1; on-ink-muted `#8E8A82` ≈ 4.6:1; magenta
  **`#EC4899` (the on-ink value, ≈ 5.1:1)** — this is *why* the spark brightens on ink,
  so never use `#DB2777` on ink (≈ 3.4:1, fails body); indigo `#7E80D2` ≈ 4.8:1.
- **Don't** put body text on the magenta spark, set indigo as a text colour on ink
  (use `#7E80D2`), or rely on colour alone to carry state — pair it with text or shape.

**Focus rings.** Every keyboard-focusable control shows a **visible magenta ring** — the
spark doubles as the focus signal. It is the **on-surface magenta**: `#DB2777` on paper,
**`#EC4899` on ink** (the DS `.plink-ink` block already remaps `--focus-ring` to the
on-ink magenta, so on-ink controls get the brighter ring for free). The ring is a **2px
outline at 2px offset** (`:focus-visible`), never removed without an equal-or-better
replacement. Focus must never be suppressed (`outline: none` with nothing in its place)
and must be visible on both the default and hover/pressed states.

**Targets & text.** Interactive targets are **≥ 44×44px** effective hit area (pad small
controls rather than shrinking the target). Respect the user's font scale; layouts wrap
and reflow rather than clip (the redesign's integration tests assert no overflow at the
real font). Provide text alternatives for the mark/ping (`aria-hidden` on purely
decorative ones, e.g. the identity rule and a static ping).

---

## 8. Motion

Motion in Anchor is **calm and meaningful** — it confirms a state change or carries the
one live signal (the ping), and it is **never decorative loops competing with the work**.
Anchor talks to a student mid-task: movement should settle attention, not pull it.

- **The ping is the one ambient motion.** The signature concentric-ring pulse
  (`.pl-ping--pulse`) marks the live session; everything else is static or a brief
  transition. On ink it uses the on-ink magenta. A non-live context uses the **static**
  ping (`.pl-ping--static`) — no animation.
- **Transitions are short and quiet** — state changes (a panel appearing, a countdown)
  use brief, eased transitions, not springy or attention-grabbing ones. No parallax, no
  auto-playing flourish, no motion purely to decorate.
- **Honour `prefers-reduced-motion`.** When the user asks for reduced motion, **stop the
  loop** — the ping holds as a static ring and transitions reduce to instant/opacity.
  The DS already gates the pulse behind `@media (prefers-reduced-motion: no-preference)`
  (so reduced-motion users get the static ring automatically); any Anchor-added motion
  must gate itself the same way. The agent (WinUI) honours the OS *animations-enabled*
  setting for the same reason — never animate when the platform says don't.

---

## 9. Assets in this folder

| File | What |
|---|---|
| `ANCHOR_BRAND.md` | This document — the source of truth. |
| `anchor-mark.svg` / `anchor-mark-dark.svg` | The Anchor mark, light / dark. |
| `anchor-lockup-light.svg` / `anchor-lockup-dark.svg` | The horizontal lockup, light / dark. |

---

## 10. Provenance & decisions

- **Issue:** plinklabs/Anchor#162 (AF1) — "Define the Anchor accent & mark". The cross-cutting
  rules (§6 Surfaces, §7 Accessibility, §8 Motion) were added by plinklabs/Anchor#165 (AF4).
- **Epic:** plinklabs/Anchor#180 — Anchor visual identity & UX redesign. Foundations AF1–AF4 =
  #162–#165; AF2 (#163) produces the icon/tray/splash/favicon assets from this mark; AF3 (#164)
  wires the DS bindings (and the accent slot above) across all surfaces; AF4 (#165) fixes the
  per-surface ink/paper treatment and the a11y + motion rules below.
- **Convention:** plink-design-system#5 (DS-5) — the per-product accent extension point this is the
  first use of.
- **Decisions taken here:** accent = **deep indigo** (`#34357A` / `#7E80D2` on ink), chosen to recede
  and stay clear of the magenta spark; mark = **anchor-from-the-ping**; one user-facing name =
  **Anchor** (with `FocusAgent` internal-only).
- **Decisions taken in AF4 (#165):** the ink/paper split is **fixed per surface, never
  system-following** (students get ink to distract less; teachers get paper); the magenta spark
  doubles as the **focus ring** (on-surface value, 2px/2px); motion is calm, the **ping** is the one
  ambient loop, and `prefers-reduced-motion` (and the OS animations setting) is honoured.
