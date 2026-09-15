#!/usr/bin/env python3
"""Generate bounded, public Discord announcement payloads from a GitHub Release."""

from __future__ import annotations

import argparse
import json
import re
import unittest
from pathlib import Path

COLOR = 0x2F81F7
FOOTER = "Ralven © 2026 • Desenvolvido pela Vemryx"
PUBLIC_SECTIONS = {
    "✨ Novidades": "✨ Novidades", "Novidades": "✨ Novidades",
    "🔧 Melhorias": "🔧 Melhorias", "Melhorias": "🔧 Melhorias",
    "🐛 Correções": "🐛 Correções", "Correções": "🐛 Correções",
    "🔒 Segurança": "🔒 Segurança", "Segurança": "🔒 Segurança",
}
SECTION_HEADING = re.compile(r"^##\s+(.+?)\s*$", re.MULTILINE)


def chunks(text: str, limit: int = 1024) -> list[str]:
    """Keep Discord field values within their documented 1024-character limit."""
    result: list[str] = []
    current = ""
    for line in (line for line in text.strip().splitlines() if line.strip()):
        while len(line) > limit:
            if current:
                result.append(current)
                current = ""
            result.append(line[:limit])
            line = line[limit:]
        if current and len(current) + len(line) + 1 > limit:
            result.append(current)
            current = line
        else:
            current = f"{current}\n{line}" if current else line
    if current:
        result.append(current)
    return result


def public_sections(notes: str) -> list[tuple[str, str]]:
    matches = list(SECTION_HEADING.finditer(notes or ""))
    sections: list[tuple[str, str]] = []
    for index, match in enumerate(matches):
        name = PUBLIC_SECTIONS.get(match.group(1).strip())
        if not name:
            continue
        end = matches[index + 1].start() if index + 1 < len(matches) else len(notes)
        content = notes[match.end():end].strip()
        if content:
            sections.extend((name, part) for part in chunks(content))
    return sections


def payloads(release: dict) -> list[dict]:
    tag = str(release.get("tag_name") or release.get("name") or "Nova versão").strip()
    prerelease = bool(release.get("prerelease"))
    channel = "Beta" if prerelease else "Stable"
    release_url = str(release.get("html_url") or "https://github.com/")
    download_url = release_url if prerelease else "https://vemryx.com/Ralven/download/"
    header = {
        "title": f"{'🧪' if prerelease else '🚀'} Ralven {tag} disponível!"[:256],
        "url": release_url,
        "description": f"Uma nova versão {'de testes' if prerelease else 'estável'} do **Ralven** acaba de ser publicada.",
        "color": COLOR,
        "fields": [
            {"name": "📦 Versão", "value": f"`{tag}`", "inline": True},
            {"name": "📣 Canal", "value": channel, "inline": True},
            {"name": "🖥️ Compatibilidade", "value": "Windows 10 e Windows 11", "inline": False},
            {"name": "📥 Download", "value": f"[{'Ver pré-release' if prerelease else 'Baixar pelo site oficial'}]({download_url})", "inline": True},
            {"name": "📖 Release completa", "value": f"[Ver no GitHub]({release_url})", "inline": True},
        ],
        "footer": {"text": FOOTER},
    }
    if release.get("published_at"):
        header["timestamp"] = release["published_at"]

    embeds = [header]
    sections = public_sections(str(release.get("body") or ""))
    if sections:
        notes = {"title": "📝 O que mudou", "color": COLOR, "fields": []}
        notes_size = len(notes["title"])
        for name, content in sections:
            if len(notes["fields"]) == 25 or notes_size + len(name) + len(content) > 5800:
                notes["footer"] = {"text": FOOTER}
                embeds.append(notes)
                notes = {"title": "📝 O que mudou • continuação", "color": COLOR, "fields": []}
                notes_size = len(notes["title"])
            notes["fields"].append({"name": name, "value": content, "inline": False})
            notes_size += len(name) + len(content)
        notes["footer"] = {"text": FOOTER}
        embeds.append(notes)
    else:
        embeds.append({
            "title": "📝 O que mudou",
            "description": "Os detalhes desta versão estão disponíveis na release completa no GitHub.",
            "color": COLOR,
            "footer": {"text": FOOTER},
        })

    return [{"username": "Ralven Updates", "allowed_mentions": {"parse": []}, "embeds": embeds[offset:offset + 10]}
            for offset in range(0, len(embeds), 10)]


class PayloadTests(unittest.TestCase):
    def test_public_sections_exclude_internal_notes_and_route_beta(self) -> None:
        release = {
            "tag_name": "v2.0.0-beta.1", "prerelease": True,
            "html_url": "https://github.com/marquezinii/Ralven/releases/tag/v2.0.0-beta.1",
            "body": "## ✨ Novidades\n- Recurso novo.\n\n## ⚙️ Alterações técnicas\n- CI interno.",
        }
        generated = payloads(release)
        fields = generated[0]["embeds"][1]["fields"]
        self.assertEqual(generated[0]["embeds"][0]["color"], COLOR)
        self.assertEqual(generated[0]["embeds"][0]["fields"][1]["value"], "Beta")
        self.assertEqual(fields, [{"name": "✨ Novidades", "value": "- Recurso novo.", "inline": False}])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(PayloadTests)
        raise SystemExit(not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful())
    if not args.release or not args.output:
        parser.error("--release and --output are required unless --self-test is used")
    args.output.write_text(json.dumps(payloads(json.loads(args.release.read_text(encoding="utf-8"))), ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
