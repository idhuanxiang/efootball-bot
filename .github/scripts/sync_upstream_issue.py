#!/usr/bin/env python3
"""Sync release notes from CHANGELOG.md into upstream Issue #10 body (full-body PATCH, no comments/PRs)."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

MARKER_START = "<!-- auto-sync-releases:start -->"
MARKER_END = "<!-- auto-sync-releases:end -->"
MAX_ENTRIES = 5


def write_summary(markdown: str) -> None:
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        return
    with open(summary_path, "a", encoding="utf-8") as f:
        f.write(markdown.rstrip() + "\n")


def gh_request(
    method: str,
    url: str,
    token: str | None,
    payload: dict[str, Any] | None = None,
    retries: int = 3,
) -> Any:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "efootball-bot-upstream-sync",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    data: bytes | None = None
    if payload is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    last_err: Exception | None = None
    for attempt in range(1, retries + 1):
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                raw = resp.read().decode("utf-8")
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8", errors="replace")
            if e.code >= 500 and attempt < retries:
                last_err = RuntimeError(f"HTTP {e.code}: {err_body}")
                time.sleep(2**attempt)
                continue
            raise RuntimeError(f"GitHub API {method} {url} failed ({e.code}): {err_body}") from e
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            last_err = e
            if attempt < retries:
                time.sleep(2**attempt)
                continue
            raise RuntimeError(f"GitHub API {method} {url} network error: {e}") from e

    raise RuntimeError(f"GitHub API {method} {url} failed: {last_err}")


def extract_changelog_section(changelog_path: Path, tag: str) -> str:
    if not changelog_path.is_file():
        return ""
    text = changelog_path.read_text(encoding="utf-8")
    pattern = re.compile(rf"(?ms)^## {re.escape(tag)}\s*?(.*?)(?=^## v|\Z)")
    match = pattern.search(text)
    if not match:
        return ""
    return match.group(1).strip()


def update_issue_body(
    current_body: str,
    tag: str,
    sha: str,
    changelog_body: str,
    source_repo: str,
) -> str:
    body = current_body or ""
    release_url = f"https://github.com/{source_repo}/releases/tag/{tag}"

    # 1. Update header sync reference if present
    header_pattern = re.compile(
        r"（最新同步至 \*\*\[v[^\]]+\]\([^)]+\)\*\*，提交 `[0-9a-f]+`）"
    )
    header_replacement = f"（最新同步至 **[{tag}]({release_url})**，提交 `{sha}`）"
    body = header_pattern.sub(header_replacement, body, count=1)

    # 2. Build idempotent release sub-block
    sub_start = f"<!-- release:{tag}:start -->"
    sub_end = f"<!-- release:{tag}:end -->"
    entry = f"{sub_start}\n#### [{tag}]({release_url})\n\n{changelog_body}\n{sub_end}"

    if MARKER_START in body and MARKER_END in body:
        block_re = re.compile(
            re.escape(MARKER_START) + r"(.*?)" + re.escape(MARKER_END),
            re.DOTALL,
        )
        m = block_re.search(body)
        inner = m.group(1).strip() if m else ""

        # Parse existing release entries in order
        entry_re = re.compile(
            r"<!-- release:(v[^:]+):start -->.*?<!-- release:\1:end -->",
            re.DOTALL,
        )
        existing_entries = [em.group(0).strip() for em in entry_re.finditer(inner)]
        sub_re = re.compile(
            re.escape(sub_start) + r".*?" + re.escape(sub_end),
            re.DOTALL,
        )

        if any(sub_re.fullmatch(item) for item in existing_entries):
            new_entries = [entry if sub_re.fullmatch(item) else item for item in existing_entries]
        else:
            new_entries = [entry] + existing_entries

        new_entries = new_entries[:MAX_ENTRIES]
        new_block = f"{MARKER_START}\n" + "\n\n".join(new_entries) + f"\n{MARKER_END}"
        body = body[: m.start()] + new_block + body[m.end() :]
    else:
        section = f"\n\n### 近期版本自动同步\n\n{MARKER_START}\n{entry}\n{MARKER_END}"
        divider_idx = body.rfind("\n---")
        if divider_idx != -1:
            body = body[:divider_idx] + section + body[divider_idx:]
        else:
            body = body.rstrip() + section + "\n"

    return body


def main() -> int:
    parser = argparse.ArgumentParser(description="Sync CHANGELOG section to upstream Issue body.")
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v9.4.0")
    parser.add_argument("--sha", required=True, help="Commit SHA")
    parser.add_argument("--changelog", default="CHANGELOG.md", help="Path to CHANGELOG.md")
    parser.add_argument("--upstream-repo", default="abababaaaa/efootball-bot")
    parser.add_argument("--issue-number", type=int, default=10)
    parser.add_argument("--source-repo", default="idhuanxiang/efootball-bot")
    parser.add_argument("--dry-run", action="store_true", help="Preview without PATCHing")
    args = parser.parse_args()

    upstream_token = os.environ.get("UPSTREAM_SYNC_TOKEN", "").strip()
    github_token = os.environ.get("GITHUB_TOKEN", "").strip()

    if not args.dry_run and not upstream_token:
        print("::warning::UPSTREAM_SYNC_TOKEN 未配置，已跳过自动同步上游 Issue #10")
        write_summary(
            """## ⏭️ 已跳过同步上游 Issue #10

未检测到 `UPSTREAM_SYNC_TOKEN` Secret，无法跨仓库更新 `abababaaaa/efootball-bot#10`。

### 配置方法（一次性）
1. 在 GitHub 生成一个 **Classic PAT**（`Tokens (classic)`），勾选 `public_repo` 权限。
2. 打开仓库 **Settings → Secrets and variables → Actions → New repository secret**。
3. Name 填 `UPSTREAM_SYNC_TOKEN`，Value 粘贴该 PAT。
4. 配置完成后，后续每次正式发版将自动同步 `CHANGELOG.md` 到上游 Issue #10 正文。"""
        )
        return 0

    token = upstream_token or github_token or None

    changelog_body = extract_changelog_section(Path(args.changelog), args.tag)
    if not changelog_body:
        print(f"::warning::CHANGELOG.md 中未找到 {args.tag} 段落，跳过同步")
        write_summary(f"## ⚠️ 跳过同步\n\n`{args.changelog}` 中未找到 `{args.tag}` 段落。")
        return 0

    issue_url = f"https://api.github.com/repos/{args.upstream_repo}/issues/{args.issue_number}"
    issue_data = gh_request("GET", issue_url, token)
    current_body = issue_data.get("body") or ""

    updated_body = update_issue_body(
        current_body=current_body,
        tag=args.tag,
        sha=args.sha,
        changelog_body=changelog_body,
        source_repo=args.source_repo,
    )

    if updated_body == current_body:
        print("正文无变更，跳过 PATCH")
        write_summary(f"## ℹ️ 正文无变更\n\n上游 Issue `{args.upstream_repo}#{args.issue_number}` 已包含 `{args.tag}` 最新内容，无需更新。")
        return 0

    if args.dry_run:
        print(f"[DRY-RUN] 将更新 {args.upstream_repo}#{args.issue_number}（标签 {args.tag}，提交 {args.sha}）")
        print(f"[DRY-RUN] 原文长度: {len(current_body)} 字符 -> 新正文长度: {len(updated_body)} 字符")
        print("--- PREVIEW START ---")
        print(updated_body)
        print("--- PREVIEW END ---")
        write_summary(
            f"## 🧪 Dry-Run 预览完成\n\n"
            f"- 目标：`{args.upstream_repo}#{args.issue_number}`\n"
            f"- 版本：`{args.tag}` (`{args.sha}`)\n"
            f"- 字符数：`{len(current_body)}` → `{len(updated_body)}`\n"
            f"- 本次为 Dry-Run，未实际发送 PATCH。"
        )
        return 0

    gh_request("PATCH", issue_url, upstream_token, payload={"body": updated_body})
    print(f"已成功全量更新 {args.upstream_repo}#{args.issue_number}（{args.tag} / {args.sha}）")
    write_summary(
        f"## ✅ 已同步上游 Issue #{args.issue_number}\n\n"
        f"- 目标：[{args.upstream_repo}#{args.issue_number}](https://github.com/{args.upstream_repo}/issues/{args.issue_number})\n"
        f"- 版本：`{args.tag}` (`{args.sha}`)\n"
        f"- 字符数：`{len(current_body)}` → `{len(updated_body)}`\n"
        f"- 方式：全量更新正文（未发评论 / 未提 PR）"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
