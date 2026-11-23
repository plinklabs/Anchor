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

It also reports, in style.css:

- contrast: a text colour below WCAG AA's 4.5:1 on its background, for each
  (text, background) pair of :root tokens in CONTRAST_PAIRS (#598). The pairs
  say which background a colour sits on; the check doesn't work that out from
  the pages. Those tokens must be #RRGGBB or #RGB values in a :root block
  outside any @media, so the check can read them.
- button: a rule for the links inside a container that reaches a button, an
  element with class="cta" on any page. Such a rule's last compound selects
  <a> without a class or id of its own, after an ancestor: ".doc a". It
  outweighs the button's own .cta--primary and .cta--secondary rules, so the
  button looks different in that container only; ".doc a" made the contact
  page's primary button magenta on magenta (#599). Write ".doc a:not(.cta)".
  Selectors are matched against each button's ancestors in the markup; a test
  the markup can't settle (:hover, ::after, [href^=...]) counts as a match,
  ">" is read as a descendant combinator, and the compound before "+" or "~"
  isn't checked.

Under GitHub Actions each problem is printed as an error annotation on its
file and line. Exits 1 if it found any problem.
.github/scripts/test-check-website.py proves that each rule still fails a page
that breaks it.

Usage: python3 .github/scripts/check-website.py [SITE_DIR]
SITE_DIR defaults to website/ in the repo this script is in.
"""

import math
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

STYLESHEET = "style.css"
# WCAG AA for normal-size text. The site's secondary text is small (captions,
# the sub-navigation and the footer are 10-11px), so no pair gets the 3:1 of
# large text.
CONTRAST_MIN = 4.5
# (text token, background token, what is written in that colour). Every piece
# of text in these colours sits on --paper: the callouts' tinted background
# holds only --ink and --magenta text. --magenta (links, card titles, the
# primary button) is 4.30:1 and isn't listed until #600 fixes it.
CONTRAST_PAIRS = (
    ("--ink", "--paper", "body text"),
    ("--muted", "--paper",
     "secondary text (hero, lead and card paragraphs, sub-navigation, captions, footer)"),
)
CSS_COMMENT = re.compile(r"/\*.*?\*/", re.S)
CSS_ROOT = re.compile(r":root\s*\{([^{}]*)\}")
CSS_CUSTOM_PROPERTY = re.compile(r"\s*(--[A-Za-z0-9_-]+)\s*:(.*)", re.S)
HEX_COLOUR = re.compile(r"#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})")
# The class every button carries; .cta--primary and .cta--secondary give it
# its colours, the same on every page.
BUTTON_CLASS = "cta"
# A style rule: its selector list (after the last ";", "{" or "}") and a body
# without nested blocks, so the rules inside @media are found too.
CSS_RULE = re.compile(r"([^{};]*)\{[^{}]*\}")
# One simple selector of a compound one.
CSS_SIMPLE = re.compile(r"""
    (?P<type>\*|[A-Za-z][\w-]*)
  | \.(?P<cls>[\w-]+)
  | \#(?P<id>[\w-]+)
  | \[\s*(?P<attr>[\w-]+)\s*
      (?:(?P<op>[~|^$*]?=)\s*(?P<value>"[^"]*"|'[^']*'|[^\]\s]+)\s*)?\]
  | ::?(?P<pseudo>[\w-]+)(?:\((?P<arg>(?:[^()]|\([^()]*\))*)\))?
""", re.X)


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
        self.buttons = []       # (line, tag, attrs, [(tag, attrs)] of its ancestors)
        self.problems = []      # (line, message)
        self._open = []         # [(tag, line, attrs)]
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
        if BUTTON_CLASS in (values.get("class") or "").split():
            self.buttons.append((line, tag, values, [(t, a) for t, _, a in self._open]))

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
        elif any(t == tag for t, *_ in self._open):
            while self._open[-1][0] != tag:
                t, opened, _ = self._open.pop()
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
            self._open.append((tag, line, dict(attrs)))

    def handle_startendtag(self, tag, attrs):
        line = self.getpos()[0]
        self._start(tag, attrs, line)
        if tag in VOID:
            return
        self._open.append((tag, line, dict(attrs)))
        if not any(t in FOREIGN for t, *_ in self._open):
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
        for tag, line, _ in self._open:
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


def blank_comments(css):
    """The stylesheet with its comments blanked out. Their newlines stay, so
    offsets still give lines."""
    return CSS_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group()), css)


def root_tokens(css):
    """{name: (value, line)} for the custom properties in the stylesheet's
    :root blocks that aren't inside @media or another at-rule. A later
    definition wins, as it does in the browser."""
    css = blank_comments(css)
    tokens = {}
    for block in CSS_ROOT.finditer(css):
        if css.count("{", 0, block.start()) != css.count("}", 0, block.start()):
            continue
        offset = block.start(1)
        for declaration in block.group(1).split(";"):
            match = CSS_CUSTOM_PROPERTY.match(declaration)
            if match:
                line = css.count("\n", 0, offset + match.start(1)) + 1
                tokens[match.group(1)] = (match.group(2).strip(), line)
            offset += len(declaration) + 1
    return tokens


def relative_luminance(colour):
    digits = colour[1:]
    if len(digits) == 3:
        digits = "".join(d * 2 for d in digits)
    r, g, b = (
        c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
        for c in (int(digits[i:i + 2], 16) / 255 for i in (0, 2, 4))
    )
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast_ratio(a, b):
    """The WCAG 2 contrast ratio of two #RRGGBB or #RGB colours, 1 to 21."""
    light, dark = sorted((relative_luminance(a), relative_luminance(b)), reverse=True)
    return (light + 0.05) / (dark + 0.05)


def check_contrast(site):
    """(problems, pairs checked) for CONTRAST_PAIRS in the site's stylesheet."""
    path = site / STYLESHEET
    if not path.is_file():
        return [], 0  # the pages' links to it report that it is missing
    tokens = root_tokens(path.read_text(encoding="utf-8"))
    problems, colours = [], {}
    for name in dict.fromkeys(n for pair in CONTRAST_PAIRS for n in pair[:2]):
        if name not in tokens:
            problems.append((
                path, 1,
                f"contrast: :root (outside @media) defines no {name}, whose contrast "
                "CONTRAST_PAIRS in .github/scripts/check-website.py checks; "
                "if it was renamed, rename it there too",
            ))
        elif not HEX_COLOUR.fullmatch(tokens[name][0]):
            problems.append((
                path, tokens[name][1],
                f'contrast: {name} is "{tokens[name][0]}", not a #RRGGBB or #RGB colour, '
                "so its contrast can't be checked",
            ))
        else:
            colours[name] = tokens[name][0]
    checked = 0
    for text, background, use in CONTRAST_PAIRS:
        if text not in colours or background not in colours:
            continue
        checked += 1
        ratio = contrast_ratio(colours[text], colours[background])
        if ratio < CONTRAST_MIN:
            shown = f"{ratio:.2f}"
            if float(shown) >= CONTRAST_MIN:  # 4.497 must not read as 4.50:1
                shown = f"{math.floor(ratio * 1000) / 1000:.3f}"
            problems.append((
                path, tokens[text][1],
                f"contrast: {text} {colours[text]} on {background} {colours[background]} "
                f"is {shown}:1, below the {CONTRAST_MIN}:1 WCAG AA asks of {use}",
            ))
    return problems, checked


def selector_list(text):
    """The selectors of a comma-separated list; a comma inside () or [] is
    part of its selector."""
    selectors, depth, current = [], 0, ""
    for ch in text:
        depth += (ch in "([") - (ch in ")]")
        if ch == "," and depth == 0:
            selectors.append(current)
            current = ""
        else:
            current += ch
    selectors.append(current)
    return [s.strip() for s in selectors if s.strip()]


def compounds(selector):
    """[(combinator, compound)] of a selector, left to right. The combinator
    joins the compound to the one before it: " ", ">", "+" or "~", and "" for
    the first."""
    parts, depth, current, combinator = [], 0, "", ""
    for ch in selector:
        depth += (ch in "([") - (ch in ")]")
        if depth == 0 and (ch.isspace() or ch in ">+~"):
            if current:
                parts.append((combinator, current))
                current, combinator = "", " "
            if ch in ">+~":
                combinator = ch
        else:
            current += ch
    if current:
        parts.append((combinator, current))
    return parts


def compound_matches(compound, tag, attrs):
    """False if the compound selector can't match the element. A test the
    markup can't settle (a pseudo-class such as :hover, a pseudo-element, an
    attribute test other than [name] and [name=value]) counts as a match."""
    classes = (attrs.get("class") or "").split()
    for m in CSS_SIMPLE.finditer(compound):
        if m["type"] and m["type"] != "*" and m["type"].lower() != tag:
            return False
        if m["cls"] and m["cls"] not in classes:
            return False
        if m["id"] and m["id"] != attrs.get("id"):
            return False
        if m["attr"]:
            name = m["attr"].lower()
            if name not in attrs:
                return False
            if m["op"] == "=" and m["value"].strip("\"'") != (attrs[name] or ""):
                return False
        if m["pseudo"] == "not" and m["arg"] is not None:
            for negated in selector_list(m["arg"]):
                plain = not re.search(r"[\s>+~:(]", negated)
                if plain and compound_matches(negated, tag, attrs):
                    return False
    return True


def reaches(parts, tag, attrs, ancestors):
    """True if the selector's compounds may match the element, whose
    ancestors are [(tag, attrs)] from <html> down. ">" is read as " ", and the
    compound before "+" or "~", a sibling, isn't checked: its ancestors are
    the next compound's."""
    if not compound_matches(parts[-1][1], tag, attrs):
        return False
    level = len(ancestors)
    for i in range(len(parts) - 2, -1, -1):
        if parts[i + 1][0] in ("+", "~"):
            continue
        # The nearest ancestor that matches leaves the most room for the
        # compounds further left.
        while level and not compound_matches(parts[i][1], *ancestors[level - 1]):
            level -= 1
        if not level:
            return False
        level -= 1
    return True


def container_link_rules(css):
    """[(selector, compounds, line)] for the stylesheet's rules for the links
    inside a container: the last compound selects <a> and has no class or id
    of its own, and a compound before it is an ancestor."""
    css = blank_comments(css)
    rules = []
    for rule in CSS_RULE.finditer(css):
        prelude = rule.group(1)
        if prelude.strip().startswith("@"):
            continue
        start = rule.start(1) + len(prelude) - len(prelude.lstrip())
        line = css.count("\n", 0, start) + 1
        for selector in selector_list(prelude):
            parts = compounds(selector)
            if not parts:
                continue
            subject = list(CSS_SIMPLE.finditer(parts[-1][1]))
            if not any((m["type"] or "").lower() == "a" for m in subject):
                continue
            if any(m["cls"] or m["id"] for m in subject):
                continue
            if all(combinator in ("+", "~") for combinator, _ in parts[1:]):
                continue
            rules.append((" ".join(selector.split()), parts, line))
    return rules


def check_buttons(site, pages):
    """(problems, buttons checked): a rule for the links inside a container
    must not reach a button, where it outweighs the button's own rules."""
    path = site / STYLESHEET
    buttons = [(page, button) for page in pages.values() for button in page.buttons]
    if not path.is_file():
        return [], len(buttons)
    problems = []
    for selector, parts, line in container_link_rules(path.read_text(encoding="utf-8")):
        reached = [(page, b) for page, b in buttons if reaches(parts, b[1], b[2], b[3])]
        if not reached:
            continue
        page, (button_line, tag, attrs, _) = reached[0]
        more = f" (and {len(reached) - 1} more)" if len(reached) > 1 else ""
        subject = parts[-1][1]
        if selector.endswith(subject) and subject[:1].lower() == "a":
            fixed = f'"{selector[:-len(subject)]}a:not(.{BUTTON_CLASS}){subject[1:]}"'
        else:
            fixed = f"add :not(.{BUTTON_CLASS}) to its a"
        problems.append((
            path, line,
            f'button: "{selector}" restyles the button <{tag} class="{attrs["class"]}"> on '
            f"{page.path.relative_to(site).as_posix()}:{button_line}{more}: a rule for the "
            f"links in a container outweighs the button's own .{BUTTON_CLASS} rules there. "
            f"Leave the buttons out of it: {fixed}",
        ))
    return problems, len(buttons)


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

    contrast_problems, stats["contrasts"] = check_contrast(site)
    problems.extend(contrast_problems)
    button_problems, stats["buttons"] = check_buttons(site, pages)
    problems.extend(button_problems)

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
        f"{stats['fragments']} fragments, {stats['images']} image sizes, "
        f"{stats['buttons']} buttons and {stats['contrasts']} text colour contrasts checked."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
