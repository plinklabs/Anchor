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
      <h2>Team</h2>
      <p>Back to <a href="index.html#intro">the intro</a> or <a href="./">home</a>.</p>
    </section>
  </main>
""" + FOOT


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
        (self.site / "style.css").write_text("body { margin: 0; }\n", encoding="utf-8")
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

    # output
    def test_problems_are_annotations_under_github_actions(self):
        self.edit("index.html", 'href="#shots"', 'href="#shot"')
        run = self.run_checker(github_actions="true")
        self.assertEqual(run.returncode, 1)
        self.assertRegex(run.stdout, r"::error file=[^,\n]*index\.html,line=\d+,title=Website check::fragment")


if __name__ == "__main__":
    unittest.main(verbosity=2)
