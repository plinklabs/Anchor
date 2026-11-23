#!/usr/bin/env python3
"""Check the public website for broken links, anchors and markup.

The site under website/ has no build step: a few hand-written pages that link
to each other's sections and each carry their own copy of the header,
sub-navigation and footer. website-deploy.yml publishes whatever reaches main,
so before this nothing caught a link to a renamed page, an
href="faq.html#..." whose id was moved or dropped, or an unclosed tag before
it was live (#583). Website CI runs this on every pull request that touches
website/**; run the same command from the repo root to check locally.

For every *.html page under the site folder it reports:

- link: a relative href or src that doesn't resolve to a file inside the site
  folder, with the exact case (the live site is case-sensitive). Links with a
  scheme (https:, mailto:, data:) are external and not checked, nor are
  root-relative ones such as the wordmark's "/": those point into the Pages
  site around this one (plinklabs.github.io), which this repo doesn't hold.
- fragment: a #fragment, on the same page or another, that names no id on its
  target page.
- duplicate id: an id used twice on one page.
- tags: an element that isn't closed, an end tag that closes nothing, or tags
  closed out of order. Every element except the void ones (img, br, meta, ...)
  must be closed explicitly. <div/> is an error too: HTML ignores the slash on
  an element that isn't void and leaves it open. Inside <svg> it is fine.
- image size: an <img> of a PNG whose width and height attributes are missing
  or aren't the file's real pixel size. The screenshot generators can render
  an image at another size, and stale attributes then give it the wrong shape
  until it loads.
- shared chrome: the <header>, <nav class="subnav"> and <footer> that are
  children of <body> must be on every page and identical, tag for tag and
  word for word, to index.html's. The one difference allowed is
  aria-current="page" in the sub-navigation, which must be on the link to the
  page itself and on no other link.

Under GitHub Actions each problem is printed as an error annotation on its
file and line. Exits 1 if it found any problem.
.github/scripts/test-check-website.py proves that each rule still fails a page
that breaks it.

Usage: python3 .github/scripts/check-website.py [SITE_DIR]
SITE_DIR defaults to website/ in the repo this script is in.
"""

import os
import re
import struct
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

REPO = Path(__file__).resolve().parents[2]
VOID = {
    "area", "base", "br", "col", "embed", "hr", "img", "input", "link",
    "meta", "source", "track", "wbr",
}
FOREIGN = {"svg", "math"}
URL_ATTRS = ("href", "src")
SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
# The shared blocks, in page order, and how each is named in messages.
CHROME = {"header": "<header>", "subnav": '<nav class="subnav">', "footer": "<footer>"}
REFERENCE_PAGE = "index.html"


class Page(HTMLParser):
    """One page's ids, URLs, images, shared blocks and tag problems."""

    def __init__(self, path):
        super().__init__(convert_charrefs=True)
        self.path = path
        self.ids = {}
        self.urls = []          # (line, value)
        self.images = []        # (line, attrs)
        self.chrome = {}        # name -> (line, [(token, line)])
        self.subnav_links = []  # (line, href, aria-current)
        self.problems = []      # (line, message)
        self._open = []         # [(tag, line)]
        self._capture = None    # [name, depth, line, tokens] inside a shared block

    def problem(self, line, message):
        self.problems.append((line, message))

    def _token(self, token, line):
        if self._capture is None:
            return
        tokens = self._capture[3]
        if token[0] == "text" and tokens and tokens[-1][0][0] == "text":
            tokens[-1] = (("text", tokens[-1][0][1] + " " + token[1]), tokens[-1][1])
        else:
            tokens.append((token, line))

    def _start(self, tag, attrs, line):
        values = dict(attrs)
        if "id" in values:
            ident = values["id"] or ""
            if ident in self.ids:
                self.problem(line, f'duplicate id "{ident}", also on line {self.ids[ident]}')
            else:
                self.ids[ident] = line
        for name, value in attrs:
            if name in URL_ATTRS and value is not None:
                self.urls.append((line, value))
        if tag == "img":
            self.images.append((line, values))

        if self._capture is None and self._open and self._open[-1][0] == "body":
            name = None
            if tag in ("header", "footer"):
                name = tag
            elif tag == "nav" and "subnav" in (values.get("class") or "").split():
                name = "subnav"
            if name in self.chrome:
                self.problem(line, f"a second {CHROME[name]} in <body>; a page has one")
            elif name:
                self._capture = [name, len(self._open), line, []]

        if self._capture is not None:
            kept = attrs
            if self._capture[0] == "subnav" and tag == "a":
                self.subnav_links.append((line, values.get("href"), values.get("aria-current")))
                kept = [a for a in attrs if a[0] != "aria-current"]
            self._token(("start", tag, tuple(sorted((n, v or "") for n, v in kept))), line)

    def _end(self, tag, line):
        self._token(("end", tag), line)
        if self._open and self._open[-1][0] == tag:
            self._open.pop()
        elif any(t == tag for t, _ in self._open):
            while self._open[-1][0] != tag:
                t, opened = self._open.pop()
                self.problem(opened, f"<{t}> is not closed before </{tag}> on line {line}")
            self._open.pop()
        else:
            self.problem(line, f"</{tag}> closes no open element")
        if self._capture is not None and len(self._open) <= self._capture[1]:
            name, _, start, tokens = self._capture
            self.chrome[name] = (start, tokens)
            self._capture = None

    def handle_starttag(self, tag, attrs):
        line = self.getpos()[0]
        self._start(tag, attrs, line)
        if tag not in VOID:
            self._open.append((tag, line))

    def handle_startendtag(self, tag, attrs):
        line = self.getpos()[0]
        self._start(tag, attrs, line)
        if tag in VOID:
            return
        self._open.append((tag, line))
        if not any(t in FOREIGN for t, _ in self._open):
            self.problem(
                line,
                f"<{tag}/> leaves <{tag}> open: HTML ignores the slash on an "
                f"element that isn't void; write <{tag}></{tag}>",
            )
        self._end(tag, line)

    def handle_endtag(self, tag):
        self._end(tag, self.getpos()[0])

    def handle_data(self, data):
        text = " ".join(data.split())
        if text:
            self._token(("text", text), self.getpos()[0])

    def finish(self):
        self.close()
        for tag, line in self._open:
            self.problem(line, f"<{tag}> is never closed")


def exists_exactly(site, target):
    """True if target is a file under site whose path matches in case too."""
    try:
        parts = target.relative_to(site).parts
    except ValueError:
        return False
    here = site
    for part in parts:
        try:
            if part not in os.listdir(here):
                return False
        except OSError:
            return False
        here = here / part
    return here.is_file()


def resolve(site, page, url):
    """(target path or None, fragment) for a local URL; None if not checked."""
    url = url.strip()
    if not url or SCHEME.match(url) or url.startswith("/"):
        return None
    parts = urlsplit(url)
    path = unquote(parts.path)
    target = page if not path else Path(os.path.normpath(page.parent / path))
    if path.endswith("/") or (path and target.is_dir()):
        target = target / "index.html"
    return target, unquote(parts.fragment)


def png_size(path):
    with open(path, "rb") as f:
        head = f.read(24)
    if len(head) < 24 or head[:8] != PNG_SIGNATURE or head[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", head[16:24])


def render(token):
    if token is None:
        return "nothing"
    if token[0] == "start":
        attrs = "".join(f' {n}="{v}"' if v else f" {n}" for n, v in token[2])
        return f"<{token[1]}{attrs}>"
    if token[0] == "end":
        return f"</{token[1]}>"
    text = token[1] if len(token[1]) <= 60 else token[1][:57] + "..."
    return f'"{text}"'


def check(site):
    """Return (problems, stats): problems are (path, line, message)."""
    site = Path(os.path.normpath(site.resolve()))
    pages = {}
    for path in sorted(site.rglob("*.html")):
        page = Page(path)
        page.feed(path.read_text(encoding="utf-8"))
        page.finish()
        pages[path] = page

    problems = []
    stats = {"pages": len(pages), "links": 0, "fragments": 0, "images": 0}

    def rel(path):
        return path.relative_to(site).as_posix()

    for path, page in pages.items():
        problems.extend((path, line, message) for line, message in page.problems)

        for line, url in page.urls:
            resolved = resolve(site, path, url)
            if resolved is None:
                continue
            target, fragment = resolved
            stats["links"] += 1
            try:
                target.relative_to(site)
            except ValueError:
                problems.append((path, line, f'link "{url}" points outside {site.name}/'))
                continue
            if not exists_exactly(site, target):
                problems.append((
                    path, line,
                    f'link "{url}" doesn\'t resolve: there is no file {rel(target)} '
                    "(names are case-sensitive on the live site)",
                ))
                continue
            if fragment and target in pages:
                stats["fragments"] += 1
                if fragment not in pages[target].ids:
                    where = "this page" if target == path else rel(target)
                    problems.append((
                        path, line, f'fragment "#{fragment}" in "{url}" names no id on {where}',
                    ))

        for line, attrs in page.images:
            resolved = resolve(site, path, attrs.get("src") or "")
            if resolved is None or resolved[0].suffix.lower() != ".png":
                continue
            target = resolved[0]
            if not exists_exactly(site, target):
                continue  # reported as a link
            stats["images"] += 1
            size = png_size(target)
            if size is None:
                problems.append((path, line, f'image "{attrs["src"]}" is not a PNG file'))
                continue
            want = {"width": str(size[0]), "height": str(size[1])}
            have = {k: attrs.get(k) for k in want}
            if have != want:
                said = " ".join(f'{k}="{v}"' for k, v in have.items() if v is not None) or "no size"
                problems.append((
                    path, line,
                    f'image "{attrs["src"]}" is {size[0]}x{size[1]} but its <img> has {said}; '
                    f'give it width="{size[0]}" height="{size[1]}"',
                ))

        for line, href, current in page.subnav_links:
            resolved = resolve(site, path, href or "")
            is_self = resolved is not None and resolved[0] == path
            if is_self and current != "page":
                problems.append((
                    path, line, 'the sub-navigation link to this page needs aria-current="page"',
                ))
            elif not is_self and current is not None:
                problems.append((
                    path, line,
                    f'aria-current on the sub-navigation link to "{href}", which is not this page',
                ))

    reference = site / REFERENCE_PAGE
    if reference not in pages and pages:
        reference = next(iter(pages))
    for name, label in CHROME.items():
        for path, page in pages.items():
            if name not in page.chrome:
                problems.append((
                    path, 1, f"has no {label} in <body>; every page carries the shared {label}",
                ))
        if reference not in pages or name not in pages[reference].chrome:
            continue
        expected = pages[reference].chrome[name][1]
        for path, page in pages.items():
            if path == reference or name not in page.chrome:
                continue
            actual = page.chrome[name][1]
            # Both start with the block's own start tag; past the shorter
            # one's end, compare with nothing at its last line.
            for i in range(max(len(expected), len(actual))):
                want = expected[i] if i < len(expected) else (None, expected[-1][1])
                got = actual[i] if i < len(actual) else (None, actual[-1][1])
                if want[0] != got[0]:
                    problems.append((
                        path, got[1],
                        f"{label} differs from {rel(reference)}'s: this page has {render(got[0])} "
                        f"where {rel(reference)}:{want[1]} has {render(want[0])}. Each page carries "
                        f"its own copy of {label}; make them all the same",
                    ))
                    break

    problems.sort(key=lambda p: (str(p[0]), p[1]))
    return problems, stats


def main():
    site = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "website"
    if not site.is_dir():
        sys.exit(f"error: {site} is not a folder")
    problems, stats = check(site)
    # Messages quote page text, which isn't all ASCII; a Windows pipe would
    # otherwise use the ANSI code page.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    for path, line, message in problems:
        try:
            shown = path.relative_to(REPO).as_posix()
        except ValueError:
            shown = str(path)
        if annotate:
            escaped = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            print(f"::error file={shown},line={line},title=Website check::{escaped}")
        else:
            print(f"{shown}:{line}: {message}")
    if problems:
        print(f"{len(problems)} problem(s) in the website.")
        return 1
    print(
        f"All {stats['pages']} pages pass: {stats['links']} local links and images, "
        f"{stats['fragments']} fragments, {stats['images']} image sizes checked."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
