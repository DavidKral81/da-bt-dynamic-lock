"""
Da BT Dynamic Lock - checks the documentation against reality.

Run it BEFORE EVERY RELEASE, together with the tests:

    py tools\\check_docs.py

Why it exists: on 18.08.2026 a release went out whose manuals told the user
to run  instalace\\DaBTDynamicLock-setup.exe  and to copy
telefon\\DaBTDynamicLock.apk  - neither path had existed since the folders
were renamed. The very first two steps of the quick start were impossible to
follow. Nothing catches that class of rot, because the tests exercise the
code, not the prose.

What it checks:
  1. every file and folder named in the docs really exists
  2. no Czech is left in files that must be English
  3. quoted phrases the docs present as program output really appear in the
     source (a translated log message silently invalidates the manual)

Every run starts with a self-test on the two paths that actually shipped
broken, plus the prose that must NOT be reported. A checker nobody checks
turns into a checker that passes no matter what - which is how the broken
release got out in the first place. If the self-test fails, the run stops
and the findings must not be trusted.

Exit code 1 on any finding, so it can gate a release.
"""

import io
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Folders that are not the project: the virtual environment and the downloaded
# Android toolchain hold thousands of files (pip alone ships an installer.py),
# and a name found in there would vouch for a path the project does not have.
SKIP_DIRS = {".git", ".venv", "_android-build", "_output", "_build",
             "__pycache__", "dist", "build"}

# Prose that ships or that anyone on GitHub reads.
DOCS = [
    "README.md",
    "docs/___INFO-CTI.txt",
    "docs/___INFO-READ.txt",
    "docs/DESIGN.md",
    "phone/signing-key-README.txt",
]

# Files that must be English. The two manuals are bilingual on purpose and
# texts.py / Texts.java hold the translations themselves, so they are exempt.
ENGLISH_ONLY = ["README.md", "docs/DESIGN.md", "start.bat", "requirements.txt",
                ".gitignore", "phone/signing-key-README.txt"]

# Files that must not contain a single accented character - see ascii_findings.
ASCII_ONLY = ["installer/build_installer.ps1", "phone/build.ps1", "start.bat"]

# Czech words that do not occur in English. Deliberately words with no
# English homograph, so a hit is never a false alarm.
CZECH = r"\b(aplikace|nastaveni|nastavení|soubor|slozka|složka|telefon|" \
        r"zamkne|zamykat|spusti|spustí|pouzij|použij|protoze|protože|" \
        r"takze|takže|ktery|který|nemuze|nemůže|poprve|poprvé|zkouseni|" \
        r"zkoušení|vysila|vysílá|prihlaseni|přihlášení)\b"

# Paths named in the docs that legitimately are not in the repository.
# The name being listed here excuses the FILE, never the folder it sits in -
# see the "NO FOLDER" finding below.
ALLOWED_MISSING = {
    "config.json": "created at runtime, never shipped",
    "dyn_lock.log": "created at runtime",
    "history.json": "created at runtime",
    "DaBTDynamicLock.exe": "built by the installer, not stored in the repo",
    "DaBTDynamicLock-setup.exe": "build output, excluded by .gitignore",
    "setup.exe": "shorthand for that build output in a comment",
    "DaBTDynamicLock.apk": "build output, excluded by .gitignore",
    "uninstall.exe": "copied next to the program at install time (UNINSTALLER_NAME)",
    "signing-key-DO-NOT-DELETE.jks": "the key itself is never committed",
    "signing-key-password.txt": "never committed, by design",
    "_output": "created by the tools when they run",
    "_android-build": "downloaded toolchain, not in the repository",
    ".venv": "created locally by the developer",
}

# Quoted phrases that are NOT our own user interface, so there is no point
# looking for them in the source.
ALLOWED_QUOTES = {
    "Dynamický zámek": "the Windows feature this program replaces, not our UI",
}

# What a file name ends with. A token without one of these, and without a
# first segment that exists here, is a word pair - "177 signals/min",
# "install/uninstall" - not a path.
SUFFIXES = {".py", ".ps1", ".bat", ".json", ".txt", ".md", ".apk", ".exe",
            ".jks", ".csv", ".java", ".xml", ".ico", ".png", ".log", ".lnk"}

PATH_LIKE = re.compile(
    r"(?<![\w/@:.-])"
    r"((?:[\w][\w.-]*[\\/])+[\w.-]*|[\w][\w.-]*\.(?:py|ps1|bat|json|txt|md|"
    r"apk|exe|jks|csv|java|xml|ico|png))")

# Absolute Windows paths point outside the project and carry spaces, so they
# have to be removed whole - otherwise "C:\Program Files\Da BT Dynamic Lock"
# leaves "Files\Da" behind and that gets reported as a missing path.
ABSOLUTE_PATH = re.compile(r"[A-Za-z]:\\[^\"'`\n]*")

# X, N or a number inside a quoted phrase stands for a value the program fills
# in; in the source that place holds "{s}" or similar.
PLACEHOLDER = re.compile(r"\b(?:X|N|\d+)\b")


_names = None


def read(relative):
    return io.open(ROOT / relative, encoding="utf-8", errors="replace").read()


def project_names():
    """Every file and folder name the project itself contains."""
    global _names
    if _names is None:
        _names = set()
        for folder, dirs, files in os.walk(ROOT):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            _names.update(files)
            _names.update(dirs)
    return _names


def looks_like_path(token):
    """A word pair joined by a slash is not a path."""
    segments = token.split("/")
    if Path(segments[-1]).suffix.lower() in SUFFIXES:
        return True
    if len(segments) == 1:
        return False
    return (ROOT / segments[0]).exists()


def resolves(token):
    """True if the token, or any tail of it, exists in the project.

    The manuals shorten long paths ("...\\Da BT Dynamic Lock\\phone\\build.ps1"),
    so anchoring on a tail is the only way to accept them. It is deliberately
    generous: it answers "does this thing exist somewhere sensible", which is
    the question the reader of the manual actually has.
    """
    segments = token.split("/")
    return any((ROOT / "/".join(segments[i:])).exists()
               for i in range(len(segments)))


def as_regex(quoted):
    """The phrase as a pattern, with placeholders left open."""
    return ".+?".join(re.escape(part) for part in PLACEHOLDER.split(quoted))


def path_findings(doc, text):
    out = []
    for line_no, line in enumerate(text.splitlines(), 1):
        line = re.sub(r"https?://\S+", " ", line)
        line = ABSOLUTE_PATH.sub(" ", line)
        for raw in PATH_LIKE.findall(line):
            token = raw.strip(" .,;:)»\"'`").replace("\\", "/").rstrip("/")
            if not token or "*" in token or "%" in token:
                continue
            if not looks_like_path(token):
                continue
            segments = token.split("/")
            # Named inside a folder that only appears at runtime - nothing
            # here can be verified, and its absence is not a finding.
            if any(seg in ALLOWED_MISSING for seg in segments[:-1]):
                continue
            if resolves(token):
                continue
            # A bare file name may live anywhere in the project.
            if len(segments) == 1 and token in project_names():
                continue
            if segments[-1] in ALLOWED_MISSING or token in ALLOWED_MISSING:
                parent = "/".join(segments[:-1])
                if not parent or resolves(parent):
                    continue
                # The file is allowed to be absent; the folder it is named in
                # is not. This is exactly what shipped broken in 1.0 - the
                # file name checked out, the folder had been renamed away.
                out.append(("NO FOLDER", f"{doc}:{line_no}",
                            f"{token} - there is no {parent}"))
                continue
            out.append(("MISSING", f"{doc}:{line_no}", token))
    return out


def czech_findings(relative, text):
    out = []
    for line_no, line in enumerate(text.splitlines(), 1):
        hit = re.search(CZECH, line, re.IGNORECASE)
        if hit:
            out.append(("CZECH", f"{relative}:{line_no}", hit.group(0)))
    return out


def ascii_findings(relative, raw):
    """PowerShell 5.1 and cmd read these files as ANSI, so one accented
    character breaks the build on a different code page. Checked as bytes on
    purpose - decoding them first is what would hide it."""
    out = []
    for line_no, line in enumerate(raw.split(b"\n"), 1):
        if any(byte > 127 for byte in line):
            out.append(("NON-ASCII", f"{relative}:{line_no}",
                        line.decode("cp1250", "replace").strip()))
    return out


def quote_findings(doc, text, source):
    out = []
    for line_no, line in enumerate(text.splitlines(), 1):
        for quoted in re.findall(r'"([^"]{8,60})"', line):
            # A single word is usually a name, not something the program says.
            if len(quoted.split()) < 2:
                continue
            if "\\" in quoted or "/" in quoted:
                continue                      # a path, not a message
            # Labels and messages start with a capital; a lower-case opening
            # means the quotes are wrapping prose mid-sentence.
            if not quoted[0].isupper():
                continue
            if quoted in ALLOWED_QUOTES:
                continue
            if re.search(as_regex(quoted), source):
                continue
            out.append(("NOT IN UI", f"{doc}:{line_no}", quoted))
    return out


def load_source():
    """Everything the programs can print or display, in one haystack."""
    windows = "\n".join(read(p) for p in ("windows/dyn_lock.py",
                                          "windows/texts.py",
                                          "installer/installer.py"))
    android = "\n".join(io.open(p, encoding="utf-8", errors="replace").read()
                        for p in (ROOT / "phone/src/java").rglob("*.java"))
    return windows + "\n" + android


# The checker checks itself first. Left column: what must be reported.
# Right column: prose that must stay silent - every one of these was a false
# alarm the first version raised.
MUST_REPORT = [
    "  1. Run  instalace\\DaBTDynamicLock-setup.exe  and follow it.",
    "  2. Copy  telefon\\DaBTDynamicLock.apk  to the phone.",
    '  The log then says "Zamykam obrazovku pomoci telefonu" and locks.',
]

MUST_STAY_SILENT = [
    "   program                   C:\\Program Files\\Da BT Dynamic Lock",
    # The whole absolute path has to go, not just its first segment: what is
    # left of it otherwise ("Lock\\uninstall.exe") reads as a project path
    # whose folder is missing.
    "   remove it with C:\\Program Files\\Da BT Dynamic Lock\\uninstall.exe",
    "   phone on the desk, mouse on    177 signals/min, gaps up to 3 s",
    "   telefon na stole, myš zapnutá     177 signálů/min, výpadky do 3 s",
    '        "...\\Da BT Dynamic Lock\\installer\\build_installer.ps1"',
    "   tests\\test_installer.py  the full install/uninstall cycle",
    '         Before locking, show a countdown  a small "Uzamknutí za X s"',
    "   windows\\dyn_lock.py       the application itself",
    "   The difference between \"at the desk\" and \"three metres away\" is 2 dB.",
]

CZECH_MUST_REPORT = "The screen locks by itself. Aplikace to udělá sama."
CZECH_MUST_STAY_SILENT = "The phone broadcasts a beacon every 100 ms."

ASCII_MUST_REPORT = "Write-Host 'Sestavuji instalátor'".encode("cp1250")
ASCII_MUST_STAY_SILENT = b"Write-Host 'Building the installer'"


def self_test(source):
    """Findings the checker must and must not produce. Returns what is wrong
    with the checker itself, empty list when it behaves."""
    def check(line):
        return (path_findings("self-test", line)
                + quote_findings("self-test", line, source))

    broken = []
    for line in MUST_REPORT:
        if not check(line):
            broken.append(f"  reported nothing, should have: {line.strip()}")
    for line in MUST_STAY_SILENT:
        found = check(line)
        if found:
            broken.append(f"  false alarm {found[0][2]!r} on: {line.strip()}")
    # The Czech check runs on other files than the two above, so it is tested
    # on its own. Both manuals are Czech on purpose and must never go through
    # it - that is why check() leaves it out.
    if not czech_findings("self-test", CZECH_MUST_REPORT):
        broken.append(f"  reported no Czech in: {CZECH_MUST_REPORT}")
    if czech_findings("self-test", CZECH_MUST_STAY_SILENT):
        broken.append(f"  found Czech in English: {CZECH_MUST_STAY_SILENT}")
    if not ascii_findings("self-test", ASCII_MUST_REPORT):
        broken.append(f"  reported no accent in: {ASCII_MUST_REPORT!r}")
    if ascii_findings("self-test", ASCII_MUST_STAY_SILENT):
        broken.append(f"  found an accent in: {ASCII_MUST_STAY_SILENT!r}")
    return broken


def main():
    source = load_source()

    broken = self_test(source)
    if broken:
        print("THE CHECKER ITSELF IS BROKEN - do not trust its findings:")
        print("\n".join(broken))
        return 2
    print(f"Self-test passed ({len(MUST_REPORT) + 2} cases must be reported, "
          f"{len(MUST_STAY_SILENT) + 2} must stay silent).\n")

    findings = []

    def section(title, found):
        print(title)
        for kind, where, what in found:
            print(f"  {kind:<9} {where}: {what}")
        if not found:
            print("  -")
        findings.extend(found)

    section("Paths named in the documentation:",
            [f for doc in DOCS for f in path_findings(doc, read(doc))])

    section("\nCzech left in files that must be English:",
            [f for rel in ENGLISH_ONLY for f in czech_findings(rel, read(rel))])

    section("\nAccented characters in files the shell reads as ANSI:",
            [f for rel in ASCII_ONLY
             for f in ascii_findings(rel, io.open(ROOT / rel, "rb").read())])

    section("\nQuoted program output that the source does not produce:",
            [f for doc in ("docs/___INFO-CTI.txt", "docs/___INFO-READ.txt")
             for f in quote_findings(doc, read(doc), source)])

    print()
    if findings:
        print(f"FINDINGS: {len(findings)}")
        return 1
    print("Documentation matches reality.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
