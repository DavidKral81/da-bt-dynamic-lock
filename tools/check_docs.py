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

Exit code 1 on any finding, so it can gate a release.
"""

import io
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

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

# Czech words that do not occur in English. Deliberately words with no
# English homograph, so a hit is never a false alarm.
CZECH = r"\b(aplikace|nastaveni|nastavení|soubor|slozka|složka|telefon|" \
        r"zamkne|zamykat|spusti|spustí|pouzij|použij|protoze|protože|" \
        r"takze|takže|ktery|který|nemuze|nemůže|poprve|poprvé|zkouseni|" \
        r"zkoušení|vysila|vysílá|prihlaseni|přihlášení)\b"

# Paths named in the docs that legitimately are not in the repository.
ALLOWED_MISSING = {
    "config.json": "created at runtime, never shipped",
    "dyn_lock.log": "created at runtime",
    "history.json": "created at runtime",
    "DaBTDynamicLock.exe": "built by the installer, not stored in the repo",
    "DaBTDynamicLock-setup.exe": "build output, excluded by .gitignore",
    "DaBTDynamicLock.apk": "build output, excluded by .gitignore",
    "signing-key-DO-NOT-DELETE.jks": "the key itself is never committed",
    "signing-key-password.txt": "never committed, by design",
    "_output": "created by the tools when they run",
    "_android-build": "downloaded toolchain, not in the repository",
    ".venv": "created locally by the developer",
}

PATH_LIKE = re.compile(
    r"(?<![\w/@:.-])"
    r"((?:[\w][\w.-]*[\\/])+[\w.-]*|[\w][\w.-]*\.(?:py|ps1|bat|json|txt|md|"
    r"apk|exe|jks|csv|java|xml|ico|png))")

findings = []


def report(kind, where, what):
    findings.append((kind, where, what))
    print(f"  {kind:<9} {where}: {what}")


def read(relative):
    return io.open(ROOT / relative, encoding="utf-8", errors="replace").read()


print("Paths named in the documentation:")
for doc in DOCS:
    text = read(doc)
    for line_no, line in enumerate(text.splitlines(), 1):
        # URLs are not file paths - and they carry slashes, so they must go
        # before anything else is matched.
        line = re.sub(r"https?://\S+", " ", line)
        for raw in PATH_LIKE.findall(line):
            token = raw.strip(" .,;:)»\"'`").replace("\\", "/").rstrip("/")
            if not token or "*" in token or "%" in token:
                continue
            # Absolute Windows paths point outside the project.
            if re.match(r"^[A-Za-z]:", token) or token.startswith("Program "):
                continue
            name = token.split("/")[-1]
            if name in ALLOWED_MISSING or token in ALLOWED_MISSING:
                continue
            if (ROOT / token).exists():
                continue
            # A bare filename may live anywhere in the project.
            if "/" not in token and any(ROOT.rglob(token)):
                continue
            report("MISSING", f"{doc}:{line_no}", token)

print("\nCzech left in files that must be English:")
for relative in ENGLISH_ONLY:
    for line_no, line in enumerate(read(relative).splitlines(), 1):
        hit = re.search(CZECH, line, re.IGNORECASE)
        if hit:
            report("CZECH", f"{relative}:{line_no}", hit.group(0))

print("\nQuoted program output that the source does not produce:")
# Everything the programs can print or display, in one haystack.
SOURCE = "\n".join(
    read(p) for p in ("windows/dyn_lock.py", "windows/texts.py",
                      "installer/installer.py")
    ) + "\n".join(
    io.open(p, encoding="utf-8", errors="replace").read()
    for p in (ROOT / "phone/src/java").rglob("*.java"))

for doc in ("docs/___INFO-CTI.txt", "docs/___INFO-READ.txt"):
    for line_no, line in enumerate(read(doc).splitlines(), 1):
        for quoted in re.findall(r'"([^"]{8,60})"', line):
            # Only phrases - a single word is usually a name, not output.
            if len(quoted.split()) < 2:
                continue
            if quoted in SOURCE:
                continue
            report("NOT IN UI", f"{doc}:{line_no}", quoted)

print()
if findings:
    print(f"FINDINGS: {len(findings)}")
    raise SystemExit(1)
print("Documentation matches reality.")
