#!/usr/bin/env python3
"""Self-test for check-website.py: every rule must fail a page that breaks it,
and a correct site must pass.

Website CI runs this before the real check (#583), so a change that makes the
checker pass everything fails CI instead of going unnoticed. Each test writes
a small two-page site to a temporary folder, breaks one thing in it, runs the
checker on it the way CI does, and expects exit code 1 and a message naming
the page and the problem. The unbroken site must exit 0. Run it from the repo
root to check locally.

Usage: python3 .github/scripts/test-check-website.py
"""

import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

CHECKER = Path(__file__).resolve().with_name("check-website.py")

HEAD = """<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>{title}</title>
  <link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg'></svg>">
  <link href="https://fonts.googleapis.com/css2?family=Fraunces&display=swap" rel="stylesheet">
  <link rel="stylesheet" href="style.css">
</head>
<body>

  <header>
    <a href="/" class="wordmark" aria-label="Home">
      <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M 10 20 A 9 9 0 1 1 13 20"/>
        <circle cx="12" cy="12" r="1.8"/>
      </svg>
      <span>plink labs</span>
    </a>
    <nav>
      <a href="https://github.com/plinklabs/Anchor">GitHub</a>
    </nav>
  </header>

  <nav class="subnav" aria-label="Site">
    <a href="index.html"{home_current}>Home</a>
    <a href="about.html"{about_current}>About</a>
  </nav>
"""

FOOT = """
  <footer>
    <p>Built in Belgium. Usable everywhere.</p>
    <nav>
      <a href="https://github.com/plinklabs">GitHub</a>
      <span>&middot;</span>
      <span>GPL-3.0</span>
    </nav>
  </footer>

</body>
</html>
"""

INDEX = HEAD.format(title="Home", home_current=' aria-current="page"', about_current="") + """
  <main>
    <section class="hero" id="intro">
      <h1>Home</h1>
      <p>Read <a href="about.html#team">about the team</a>, jump to <a href="#shots">the screenshots</a>, or mail <a href="mailto:someone@example.org">us</a>.</p>
      <!-- <a href="gone.html#nowhere">a commented-out link is not checked</a> -->
    </section>

    <figure class="screenshot" id="shots">
      <img src="assets/shot.png" alt="A screenshot" width="3" height="2">
      <figcaption>A <a href="about.html">screenshot</a>.<br>Second line.</figcaption>
    </figure>
  </main>
""" + FOOT

ABOUT = HEAD.format(title="About", home_current="", about_current=' aria-current="page"') + """
  <main class="doc">
    <section id="team">
      <p class="eyebrow">Who we are</p>
      <h2>Team</h2>
      <p>Back to <a href="index.html#intro">the intro</a> or <a href="./">home</a>.</p>
    </section>

    <section class="callout">
      <p class="eyebrow">Get in touch</p>
      <h2>Questions?</h2>
      <p>Ask them <a href="https://github.com/plinklabs/Anchor/issues">on GitHub</a>.</p>
      <div class="cta-row">
        <a class="cta cta--primary" href="https://github.com/plinklabs/Anchor/issues/new">Open an issue</a>
      </div>
    </section>
  </main>
""" + FOOT


# The comment ahead of --muted proves comments are skipped: read as part of
# the declaration, it would hide --muted from the contrast check. So do the
# ones ahead of the .doc rules, which would read as rules that reach into the
# button and the callout, and as a text colour the contrast pairs don't
# check. The global link rules and ".callout p a" (the button isn't in a <p>)
# don't reach the button either, and the .callout rules reach into the
# callout through the callout itself. The paragraph rules leave the eyebrows
# out, and the eyebrow rules name them. --magenta colours only borders:
# "border-color" and "background-color" aren't text colours. The hero, the
# screenshot and the prose column (.doc) hold the side gutter once each; the
# callout takes it only outside .doc, whose padding holds it already.
STYLE = """/* Design tokens */
:root {
  --ink:    #1B1B23;
  --paper:  #FAF7F2;
  /* was --muted: #9A958B, 2.79:1 on paper */
  --muted:  #6E6A62;
  --magenta: #DB2777;
  --magenta-press: #C01F68;
  --magenta-deep: #A8195B;
  --callout: #F9EFED;
  --border: 1px solid rgba(27, 27, 35, 0.15);
  --gutter: clamp(1.25rem, 5vw, 3rem);
}

body { margin: 0; background: var(--paper); color: var(--ink); }
a { color: inherit; text-decoration: none; }
a:hover { color: var(--magenta-press); }

.hero { padding: 3rem var(--gutter) 2rem; }
.screenshot { margin: clamp(2rem, 5vw, 3rem) var(--gutter); }
.doc { padding: 3rem var(--gutter) 2rem; }

.cta { border: 1.5px solid var(--magenta); }
.cta--primary { background: var(--magenta-press); color: var(--paper); }
.cta--primary:hover { background-color: var(--magenta-deep); border-color: var(--magenta-deep); }
.callout { margin: 2rem 0; background: var(--callout); border-left: 3px solid var(--magenta); }
/* .callout { margin: 2rem var(--gutter); } put the gutter twice (#604) */
.callout:not(.doc *) { margin-inline: var(--gutter); }
.callout .eyebrow { margin-bottom: 0.75rem; }
.callout h2 { margin-bottom: 0.5rem; }
.callout p:not(.eyebrow) { font-size: 0.9375rem; line-height: 1.65; }
.callout p + p:not(.eyebrow) { margin-top: 0.75rem; }
.callout p a { color: var(--magenta-press); }

/* .doc a { color: #DB2777; } reached the button (#599), .doc h2 { margin:
   2.5rem 0 0.75rem; } the callout's heading (#602), .doc p { font-size:
   0.9375rem; } the eyebrows (#603) */
.doc .eyebrow:not(.callout *) { margin-bottom: 1rem; }
.doc h2:not(.callout *) { margin: 2.5rem 0 0.75rem; }
.doc p:not(.callout *, .eyebrow) { font-size: 0.9375rem; line-height: 1.7; }
.doc a:not(.cta):not(.callout *) { color: var(--magenta-press); }
.doc a:not(.cta):not(.callout *):hover { text-decoration: underline; }
"""


def png(width, height):
    def chunk(kind, data):
        return (struct.pack(">I", len(data)) + kind + data
                + struct.pack(">I", zlib.crc32(kind + data)))
    rows = b"".join(b"\x00" + b"\x00\x00\x00" * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows))
            + chunk(b"IEND", b""))


class CheckWebsiteTest(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="check-website-test-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.site = self.root / "website"
        (self.site / "assets").mkdir(parents=True)
        (self.site / "index.html").write_text(INDEX, encoding="utf-8")
        (self.site / "about.html").write_text(ABOUT, encoding="utf-8")
        (self.site / "style.css").write_text(STYLE, encoding="utf-8")
        (self.site / "assets" / "shot.png").write_bytes(png(3, 2))
        (self.root / "outside.html").write_text(ABOUT, encoding="utf-8")

    def run_checker(self, github_actions=""):
        # Plain "file:line: message" lines unless a test asks for annotations:
        # the fixtures' expected problems must not show up on the CI run.
        env = dict(os.environ, GITHUB_ACTIONS=github_actions)
        return subprocess.run(
            [sys.executable, str(CHECKER), str(self.site)],
            capture_output=True, text=True, encoding="utf-8", env=env,
        )

    def edit(self, page, old, new):
        path = self.site / page
        text = path.read_text(encoding="utf-8")
        if old not in text:
            raise AssertionError(f"fixture {page} has no {old!r}; fix the test's edit")
        path.write_text(text.replace(old, new, 1), encoding="utf-8")

    def assert_flags(self, page, message):
        run = self.run_checker()
        self.assertEqual(run.returncode, 1, f"checker passed a broken site:\n{run.stdout}{run.stderr}")
        pattern = re.escape(page) + r":\d+: .*" + re.escape(message)
        self.assertRegex(run.stdout, pattern)
        return run

    # The correct site, with external, root-relative, data: and mailto: links,
    # comments and SVG's self-closed elements, must pass.
    def test_correct_site_passes(self):
        run = self.run_checker()
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn("All 2 pages pass", run.stdout)

    # link
    def test_link_to_a_missing_page(self):
        self.edit("index.html", 'href="about.html">screenshot', 'href="abuot.html">screenshot')
        self.assert_flags("index.html", 'link "abuot.html" doesn\'t resolve')

    def test_missing_image(self):
        self.edit("index.html", 'src="assets/shot.png"', 'src="assets/shot-old.png"')
        self.assert_flags("index.html", 'link "assets/shot-old.png" doesn\'t resolve')

    def test_link_in_the_wrong_case(self):
        self.edit("index.html", 'href="about.html">screenshot', 'href="About.html">screenshot')
        self.assert_flags("index.html", 'link "About.html" doesn\'t resolve')

    def test_link_outside_the_site(self):
        self.edit("about.html", 'href="./">home', 'href="../outside.html">home')
        self.assert_flags("about.html", 'link "../outside.html" points outside website/')

    # fragment
    def test_broken_fragment_on_the_same_page(self):
        self.edit("index.html", 'href="#shots"', 'href="#shot"')
        self.assert_flags("index.html", 'fragment "#shot" in "#shot" names no id on this page')

    def test_broken_fragment_on_another_page(self):
        self.edit("index.html", 'href="about.html#team"', 'href="about.html#staff"')
        self.assert_flags("index.html", 'fragment "#staff" in "about.html#staff" names no id on about.html')

    def test_removed_id_breaks_the_links_to_it(self):
        self.edit("about.html", '<section id="team">', "<section>")
        self.assert_flags("index.html", 'fragment "#team" in "about.html#team" names no id on about.html')

    # duplicate id
    def test_duplicate_id(self):
        self.edit("about.html", "<h2>Team</h2>", '<h2 id="team">Team</h2>')
        self.assert_flags("about.html", 'duplicate id "team"')

    # tags
    def test_unclosed_tag(self):
        self.edit("index.html", "    </section>\n", "")
        self.assert_flags("index.html", "<section> is not closed before </main>")

    def test_tag_never_closed(self):
        self.edit("about.html", "</html>", "")
        self.assert_flags("about.html", "<html> is never closed")

    def test_end_tag_that_closes_nothing(self):
        self.edit("about.html", "</section>", "</section></div>")
        self.assert_flags("about.html", "</div> closes no open element")

    def test_self_closed_element_that_is_not_void(self):
        self.edit("index.html", "<h1>Home</h1>", '<div class="spacer"/><h1>Home</h1>')
        self.assert_flags("index.html", "<div/> leaves <div> open")

    # image size
    def test_wrong_image_size(self):
        self.edit("index.html", 'width="3" height="2"', 'width="4" height="2"')
        self.assert_flags("index.html", 'image "assets/shot.png" is 3x2 but its <img> has width="4" height="2"')

    def test_image_without_a_size(self):
        self.edit("index.html", ' width="3" height="2"', "")
        self.assert_flags("index.html", 'image "assets/shot.png" is 3x2 but its <img> has no size')

    # shared chrome
    def test_header_differs(self):
        self.edit("about.html", "<span>plink labs</span>", "<span>plink lab</span>")
        self.assert_flags("about.html", '<header> differs from index.html\'s: this page has "plink lab"')

    def test_subnav_differs(self):
        self.edit("about.html", ">About</a>", ">About us</a>")
        self.assert_flags("about.html", '<nav class="subnav"> differs from index.html\'s: this page has "About us"')

    def test_footer_differs(self):
        self.edit("about.html", '<a href="https://github.com/plinklabs">', '<a href="https://github.com/plinklabs/Anchor">')
        self.assert_flags("about.html", "<footer> differs from index.html's")

    def test_footer_missing(self):
        self.edit("about.html", FOOT.split("</body>")[0], "\n")
        self.assert_flags("about.html", "has no <footer> in <body>")

    def test_subnav_without_aria_current_on_its_own_link(self):
        self.edit("about.html", ' aria-current="page"', "")
        self.assert_flags("about.html", 'the sub-navigation link to this page needs aria-current="page"')

    def test_subnav_with_aria_current_on_another_page(self):
        self.edit("about.html", '<a href="index.html">', '<a href="index.html" aria-current="page">')
        self.assert_flags("about.html", 'aria-current on the sub-navigation link to "index.html"')

    # contrast
    def test_text_colour_below_wcag_aa(self):
        self.edit("style.css", "--muted:  #6E6A62;", "--muted:  #9A958B;")
        self.assert_flags("style.css", "contrast: --muted #9A958B on --paper #FAF7F2 is 2.79:1, below the 4.5:1")

    def test_ratio_just_short_of_the_minimum_does_not_round_up_to_it(self):
        self.edit("style.css", "--muted:  #6E6A62;", "--muted:  #60748B;")
        self.assert_flags("style.css", "contrast: --muted #60748B on --paper #FAF7F2 is 4.498:1")

    def test_background_below_wcag_aa(self):
        self.edit("style.css", "--paper:  #FAF7F2;", "--paper:  #888;")
        self.assert_flags("style.css", "contrast: --muted #6E6A62 on --paper #888 is")

    def test_contrast_token_missing(self):
        self.edit("style.css", "  --muted:  #6E6A62;\n", "")
        self.assert_flags("style.css", "contrast: :root (outside @media) defines no --muted")

    def test_contrast_token_that_is_not_a_hex_colour(self):
        self.edit("style.css", "--muted:  #6E6A62;", "--muted:  rgba(27, 27, 35, 0.6);")
        self.assert_flags("style.css", 'contrast: --muted is "rgba(27, 27, 35, 0.6)", not a #RRGGBB or #RGB colour')

    def test_contrast_token_only_in_a_media_query(self):
        self.edit("style.css", "  --muted:  #6E6A62;\n", "")
        self.edit("style.css", "body {", "@media (min-width: 1px) { :root { --muted: #6E6A62; } }\nbody {")
        self.assert_flags("style.css", "contrast: :root (outside @media) defines no --muted")

    # The site's magenta text (#600): --magenta itself is 4.30:1 on paper.
    def test_magenta_text_below_wcag_aa(self):
        self.edit("style.css", "--magenta-press: #C01F68;", "--magenta-press: #DB2777;")
        self.assert_flags("style.css", "contrast: --magenta-press #DB2777 on --paper #FAF7F2 is 4.30:1, below the 4.5:1")

    def test_magenta_text_that_passes_on_paper_but_not_on_the_callout_tint(self):
        self.edit("style.css", "--magenta-press: #C01F68;", "--magenta-press: #D02470;")
        run = self.assert_flags("style.css", "contrast: --magenta-press #D02470 on --callout #F9EFED is 4.46:1")
        self.assertNotIn("on --paper", run.stdout)

    def test_button_label_below_wcag_aa_on_hover(self):
        self.edit("style.css", "--magenta-deep: #A8195B;", "--magenta-deep: #DB2777;")
        self.assert_flags("style.css", "contrast: --paper #FAF7F2 on --magenta-deep #DB2777 is 4.30:1")

    def test_text_in_a_colour_the_pairs_do_not_check(self):
        self.edit("style.css", ".callout p a { color: var(--magenta-press); }", ".callout p a { color: var(--magenta); }")
        self.assert_flags("style.css", "contrast: \"color: var(--magenta)\" isn't one of the text colours")

    def test_text_in_a_colour_that_is_not_a_token(self):
        self.edit("style.css", "a:hover { color: var(--magenta-press); }", "a:hover { color: #C01F68 !important; }")
        self.assert_flags("style.css", "contrast: \"color: #C01F68\" isn't one of the text colours")

    # button
    def test_link_rule_of_a_container_reaches_a_button(self):
        self.edit("style.css", ".doc a:not(.cta):not(.callout *) {", ".doc a {")
        run = self.assert_flags("style.css", 'button: ".doc a" restyles the button <a class="cta cta--primary"> on about.html:')
        self.assertIn('Leave the buttons out of it: ".doc a:not(.cta)"', run.stdout)

    def test_hover_rule_of_a_container_reaches_a_button(self):
        self.edit("style.css", ".doc a:not(.cta):not(.callout *):hover {", ".doc a:hover {")
        run = self.assert_flags("style.css", 'button: ".doc a:hover" restyles the button <a class="cta cta--primary"> on about.html:')
        self.assertIn('Leave the buttons out of it: ".doc a:not(.cta):hover"', run.stdout)

    def test_link_rule_in_a_selector_list_inside_a_media_query(self):
        self.edit("style.css", ".cta {", "@media (min-width: 1px) {\n  .hero a, main section > div a { outline: 0; }\n}\n.cta {")
        self.assert_flags("style.css", 'button: "main section > div a" restyles the button')

    # callout (#602)
    def test_heading_rule_of_a_container_reaches_into_a_callout(self):
        self.edit("style.css", ".doc h2:not(.callout *) {", ".doc h2 {")
        run = self.assert_flags("style.css", 'callout: ".doc h2" restyles the <h2> in the callout <section class="callout"> on about.html:')
        self.assertIn('Leave the callouts out of it: ".doc h2:not(.callout *)"', run.stdout)

    def test_class_rule_of_a_container_reaches_into_a_callout(self):
        self.edit("style.css", ".doc .eyebrow:not(.callout *) {", ".doc .eyebrow {")
        self.assert_flags("style.css", 'callout: ".doc .eyebrow" restyles the <p class="eyebrow"> in the callout')

    def test_link_rule_of_a_container_reaches_into_a_callout_but_not_its_button(self):
        self.edit("style.css", ".doc a:not(.cta):not(.callout *) {", ".doc a:not(.cta) {")
        run = self.assert_flags("style.css", 'callout: ".doc a:not(.cta)" restyles the <a> in the callout')
        self.assertNotIn("button:", run.stdout)

    def test_rule_of_a_container_that_reaches_a_callout_itself(self):
        self.edit("style.css", ".cta {", "main section { padding: 0; }\n.cta {")
        self.assert_flags("style.css", 'callout: "main section" restyles the callout <section class="callout"> on about.html:')

    # eyebrow (#603): the eyebrow is a <p>, so a container's paragraph rule
    # reaches it, and outweighs ".eyebrow".
    def test_paragraph_rule_of_a_callout_reaches_its_eyebrow(self):
        self.edit("style.css", ".callout p:not(.eyebrow) {", ".callout p {")
        run = self.assert_flags("style.css", 'eyebrow: ".callout p" restyles the eyebrow <p class="eyebrow"> on about.html:')
        self.assertIn('Leave the eyebrows out of it: ".callout p:not(.eyebrow)"', run.stdout)
        self.assertNotIn("callout:", run.stdout)

    # The eyebrow goes into the rule's :not(): a second one would add a
    # class's weight, and ".doc p" would then beat ".doc .lead".
    def test_paragraph_rule_of_a_container_reaches_an_eyebrow(self):
        self.edit("style.css", ".doc p:not(.callout *, .eyebrow) {", ".doc p:not(.callout *) {")
        run = self.assert_flags("style.css", 'eyebrow: ".doc p:not(.callout *)" restyles the eyebrow <p class="eyebrow"> on about.html:')
        self.assertIn('Leave the eyebrows out of it: ".doc p:not(.callout *, .eyebrow)"', run.stdout)

    # A rule that names the callout is written for it, and one that reaches
    # into it only through its own elements ("p a": its <p>, "section h2":
    # the callout is a <section>) styles every callout the same.
    def test_rules_that_reach_a_callout_through_the_callout_pass(self):
        self.edit("style.css", ".cta {", ".doc .callout h2 { margin-top: 0; }\np a { text-underline-offset: 2px; }\nsection h2 { letter-spacing: 0; }\n.cta {")
        run = self.run_checker()
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    # gutter (#604): .doc's padding holds the side gutter, and a callout in it
    # that takes the gutter as well sits a gutter in from the prose.
    def test_callout_that_takes_the_gutter_inside_the_prose_column(self):
        self.edit("style.css", ".callout:not(.doc *) {", ".callout {")
        run = self.assert_flags("style.css", 'gutter: ".callout" gives the <section class="callout"> on about.html:')
        self.assertIn('inside the <main class="doc">, which has it already from ".doc"', run.stdout)
        self.assertIn('Give the gutter once: ".callout:not(.doc *)"', run.stdout)

    def test_gutter_in_the_margin_shorthand_inside_the_prose_column(self):
        self.edit("style.css", ".callout { margin: 2rem 0;", ".callout { margin: 2rem var(--gutter) !important;")
        self.assert_flags("style.css", 'gutter: ".callout" gives the <section class="callout"> on about.html:')

    # A rule written for the column's elements can't leave the column out.
    def test_left_gutter_of_a_rule_for_the_prose_column(self):
        self.edit("style.css", ".doc h2:not(.callout *) { margin: 2.5rem 0 0.75rem; }",
                  ".doc h2:not(.callout *) { margin: 2.5rem 0 0.75rem calc(var(--gutter) / 2); }")
        run = self.assert_flags("style.css", 'gutter: ".doc h2:not(.callout *)" gives the <h2> on about.html:')
        self.assertIn('leave it out of ".doc h2:not(.callout *)", as ".doc" holds it', run.stdout)

    # A gutter above and below is not a side gutter.
    def test_gutter_above_and_below_inside_the_prose_column_passes(self):
        self.edit("style.css", ".callout { margin: 2rem 0;", ".callout { margin: var(--gutter) 0 var(--gutter);")
        self.edit("style.css", ".callout:not(.doc *) { margin-inline:", ".callout { margin-block:")
        run = self.run_checker()
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    # output
    def test_problems_are_annotations_under_github_actions(self):
        self.edit("index.html", 'href="#shots"', 'href="#shot"')
        run = self.run_checker(github_actions="true")
        self.assertEqual(run.returncode, 1)
        self.assertRegex(run.stdout, r"::error file=[^,\n]*index\.html,line=\d+,title=Website check::fragment")


if __name__ == "__main__":
    unittest.main(verbosity=2)
