"""Compute snapshot metadata; versioned publication requires a matching tag push."""
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def resolve(base, event, ref, run_number, run_id, attempt):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", base):
        raise ValueError("Invalid project SemVer")
    if event == "push" and ref.startswith("refs/tags/"):
        tag = ref.removeprefix("refs/tags/")
        if tag != "v" + base:
            raise ValueError("Release tag must exactly match the project version")
        return base, tag, "-" in base
    version = base + ("." if "-" in base else "-") + f"snapshot.{run_number}.{attempt}"
    return version, f"snapshot-{run_id}-{attempt}", True


if __name__ == "__main__":
    base = ET.parse("src/BetterComputerUse/BetterComputerUse.csproj").findtext("./PropertyGroup/Version")
    version, tag, prerelease = resolve(base, os.environ["GITHUB_EVENT_NAME"], os.environ["GITHUB_REF"],
        os.environ["GITHUB_RUN_NUMBER"], os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    if os.environ["GITHUB_EVENT_NAME"] == "push" and os.environ["GITHUB_REF"].startswith("refs/tags/"):
        if not Path(f"docs/release-notes/{tag}.md").is_file():
            raise ValueError("A tagged release requires docs/release-notes/<tag>.md")
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"version={version}\nrelease_tag={tag}\nprerelease={str(prerelease).lower()}\n")
