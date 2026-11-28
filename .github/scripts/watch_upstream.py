#!/usr/bin/env python3
"""Monitor upstream repository activity and maintain a tracking issue in the fork repo."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

STATE_PATTERN = re.compile(r"<!-- upstream-watch-state:\s*(\{.*?\})\s*-->", re.DOTALL)


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
    allow_404: bool = False,
) -> Any:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "efootball-bot-upstream-watch",
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
            if allow_404 and e.code == 404:
                return None
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


def send_webhook(webhook_url: str, text: str, title: str) -> None:
    payload = {
        "msg_type": "text",
        "content": {"text": f"{title}\n\n{text}"},
        "text": f"{title}\n\n{text}",
        "title": title,
        "desp": text,
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        webhook_url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            print(f"Webhook 推送成功: HTTP {resp.status}")
    except Exception as e:  # noqa: BLE001
        print(f"::warning::Webhook 推送失败（已忽略，不影响监控任务）: {e}")


def ensure_label(target_repo: str, label: str, token: str) -> None:
    encoded = urllib.parse.quote(label, safe="")
    url = f"https://api.github.com/repos/{target_repo}/labels/{encoded}"
    existing = gh_request("GET", url, token, allow_404=True)
    if existing is not None:
        return
    create_url = f"https://api.github.com/repos/{target_repo}/labels"
    gh_request(
        "POST",
        create_url,
        token,
        payload={
            "name": label,
            "color": "1d76db",
            "description": "自动化追踪上游仓库动态",
        },
    )


def find_tracking_issue(target_repo: str, label: str, token: str | None) -> dict[str, Any] | None:
    encoded_label = urllib.parse.quote(label, safe="")
    url = f"https://api.github.com/repos/{target_repo}/issues?state=open&labels={encoded_label}&per_page=10"
    issues = gh_request("GET", url, token)
    for item in issues:
        if "pull_request" not in item:
            return item
    return None


def parse_state(body: str) -> dict[str, Any] | None:
    m = STATE_PATTERN.search(body or "")
    if not m:
        return None
    try:
        return json.loads(m.group(1))
    except json.JSONDecodeError:
        return None


def build_dashboard_body(
    upstream_repo: str,
    upstream_issue: int,
    repo_meta: dict[str, Any],
    commits: list[dict[str, Any]],
    releases: list[dict[str, Any]],
    issue_comments: list[dict[str, Any]],
    open_issues: list[dict[str, Any]],
    state: dict[str, Any],
) -> str:
    latest_commit = commits[0] if commits else {}
    latest_sha = (latest_commit.get("sha") or "")[:7]
    latest_msg = ((latest_commit.get("commit") or {}).get("message") or "").splitlines()[0] if latest_commit else "-"
    latest_commit_url = latest_commit.get("html_url") or f"https://github.com/{upstream_repo}"

    latest_rel = releases[0] if releases else {}
    latest_tag = latest_rel.get("tag_name") or "暂无"
    latest_rel_url = latest_rel.get("html_url") or f"https://github.com/{upstream_repo}/releases"

    lines = [
        f"自动追踪上游仓库 [{upstream_repo}](https://github.com/{upstream_repo}) 的代码提交、Release 发布、Issue #{upstream_issue} 回复与新开 Issue。",
        "",
        "### 当前基线快照",
        f"- **默认分支**：`{repo_meta.get('default_branch', 'main')}`（最近推送 `{repo_meta.get('pushed_at', '-')}`）",
        f"- **最新提交**：[`{latest_sha}`]({latest_commit_url}) {latest_msg}",
        f"- **最新 Release**：[{latest_tag}]({latest_rel_url})",
        f"- **上游 Issue #{upstream_issue} 评论数**：`{len(issue_comments)}`",
        "",
        "### 上游当前开放 Issue",
    ]
    if open_issues:
        for iss in open_issues:
            lines.append(f"- #{iss['number']} [{iss['title']}]({iss['html_url']})")
    else:
        lines.append("- 暂无开放 Issue")

    state_json = json.dumps(state, ensure_ascii=False, separators=(",", ":"))
    lines.append("")
    lines.append(f"<!-- upstream-watch-state: {state_json} -->")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Watch upstream GitHub repo for updates.")
    parser.add_argument("--upstream-repo", default="abababaaaa/efootball-bot")
    parser.add_argument("--target-repo", default="idhuanxiang/efootball-bot")
    parser.add_argument("--upstream-issue", type=int, default=10)
    parser.add_argument("--label", default="upstream-watch")
    parser.add_argument("--force-notify", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    token = os.environ.get("GITHUB_TOKEN", "").strip() or None
    webhook_url = os.environ.get("NOTIFY_WEBHOOK_URL", "").strip()

    if not args.dry_run and not token:
        raise RuntimeError("GITHUB_TOKEN 未配置，无法读写追踪 Issue")

    base = f"https://api.github.com/repos/{args.upstream_repo}"
    repo_meta = gh_request("GET", base, token)
    default_branch = repo_meta.get("default_branch", "main")
    commits = gh_request("GET", f"{base}/commits?sha={urllib.parse.quote(default_branch)}&per_page=10", token)
    releases = gh_request("GET", f"{base}/releases?per_page=5", token)
    issue_comments = gh_request(
        "GET", f"{base}/issues/{args.upstream_issue}/comments?per_page=30", token
    )
    raw_issues = gh_request(
        "GET", f"{base}/issues?state=open&sort=updated&direction=desc&per_page=10", token
    )
    open_issues = [i for i in raw_issues if "pull_request" not in i]

    latest_commit_sha = commits[0]["sha"] if commits else ""
    latest_release_tag = releases[0]["tag_name"] if releases else ""
    last_comment_id = issue_comments[-1]["id"] if issue_comments else 0
    known_issue_numbers = sorted([i["number"] for i in open_issues])

    new_state = {
        "last_commit_sha": latest_commit_sha,
        "last_release_tag": latest_release_tag,
        "last_issue10_comment_id": last_comment_id,
        "known_issue_numbers": known_issue_numbers,
    }

    dashboard_body = build_dashboard_body(
        upstream_repo=args.upstream_repo,
        upstream_issue=args.upstream_issue,
        repo_meta=repo_meta,
        commits=commits,
        releases=releases,
        issue_comments=issue_comments,
        open_issues=open_issues,
        state=new_state,
    )

    tracking_issue = find_tracking_issue(args.target_repo, args.label, token)

    if tracking_issue is None:
        if args.dry_run:
            print(f"[DRY-RUN] 未找到现有追踪 Issue，将创建标签 `{args.label}` 并新建基线追踪 Issue。")
            print(f"[DRY-RUN] 基线提交={latest_commit_sha[:7]}, Release={latest_release_tag}, Issue#{args.upstream_issue} 评论数={len(issue_comments)}, 开放 Issue={known_issue_numbers}")
            return 0

        assert token is not None
        ensure_label(args.target_repo, args.label, token)
        created = gh_request(
            "POST",
            f"https://api.github.com/repos/{args.target_repo}/issues",
            token,
            payload={
                "title": f"[上游监控] {args.upstream_repo} 动态追踪",
                "body": dashboard_body,
                "labels": [args.label],
            },
        )
        print(f"已创建基线追踪 Issue #{created['number']}: {created['html_url']}")
        write_summary(
            f"## 🆕 已建立上游监控基线\n\n"
            f"- 追踪 Issue：[#{created['number']}]({created['html_url']})\n"
            f"- 最新提交：`{latest_commit_sha[:7]}`\n"
            f"- 最新 Release：`{latest_release_tag or '暂无'}`\n"
            f"- 上游 Issue #{args.upstream_issue} 评论数：`{len(issue_comments)}`"
        )
        return 0

    prev_state = parse_state(tracking_issue.get("body") or "") or {}
    prev_sha = prev_state.get("last_commit_sha", "")
    prev_tag = prev_state.get("last_release_tag", "")
    prev_comment_id = int(prev_state.get("last_issue10_comment_id", 0) or 0)
    prev_issues = set(prev_state.get("known_issue_numbers") or [])

    new_commits: list[dict[str, Any]] = []
    if prev_sha:
        for c in commits:
            if c["sha"] == prev_sha:
                break
            new_commits.append(c)

    new_release = latest_release_tag if (latest_release_tag and latest_release_tag != prev_tag) else ""
    new_comments = [c for c in issue_comments if int(c["id"]) > prev_comment_id]
    new_issues = [i for i in open_issues if i["number"] not in prev_issues]

    has_changes = bool(new_commits or new_release or new_comments or new_issues)

    if not has_changes and not args.force_notify:
        print("上游暂无新动态")
        write_summary(
            f"## ✅ 上游暂无新动态\n\n"
            f"- 追踪 Issue：[#{tracking_issue['number']}]({tracking_issue['html_url']})\n"
            f"- 当前基线：`{latest_commit_sha[:7]}` / `{latest_release_tag or '暂无'}`"
        )
        return 0

    digest_lines = [f"### 上游动态更新（{args.upstream_repo}）", ""]
    if new_commits:
        digest_lines.append(f"#### 🆕 新增提交（{len(new_commits)} 条）")
        for c in new_commits:
            msg = ((c.get("commit") or {}).get("message") or "").splitlines()[0]
            digest_lines.append(f"- [`{c['sha'][:7]}`]({c['html_url']}) {msg}")
        digest_lines.append("")
    if new_release:
        rel_url = releases[0].get("html_url") or f"https://github.com/{args.upstream_repo}/releases"
        digest_lines.append(f"#### 🚀 新 Release：[{new_release}]({rel_url})")
        digest_lines.append("")
    if new_comments:
        digest_lines.append(f"#### 💬 Issue #{args.upstream_issue} 新增评论（{len(new_comments)} 条）")
        for cm in new_comments:
            user = (cm.get("user") or {}).get("login", "unknown")
            snippet = (cm.get("body") or "").strip().replace("\r\n", " ").replace("\n", " ")[:120]
            digest_lines.append(f"- **@{user}**: {snippet} ([查看]({cm['html_url']}))")
        digest_lines.append("")
    if new_issues:
        digest_lines.append(f"#### 📌 新开 Issue（{len(new_issues)} 个）")
        for iss in new_issues:
            digest_lines.append(f"- #{iss['number']} [{iss['title']}]({iss['html_url']})")
        digest_lines.append("")
    if not has_changes and args.force_notify:
        digest_lines.append("- 本次为手动强制检查：上游暂无新提交、新 Release 或新评论。")

    digest_md = "\n".join(digest_lines).strip()

    if args.dry_run:
        print("[DRY-RUN] 检测到以下动态（未实际修改追踪 Issue）：")
        print(digest_md)
        return 0

    assert token is not None
    issue_api = f"https://api.github.com/repos/{args.target_repo}/issues/{tracking_issue['number']}"
    gh_request("PATCH", issue_api, token, payload={"body": dashboard_body})
    gh_request("POST", f"{issue_api}/comments", token, payload={"body": digest_md})

    if webhook_url:
        send_webhook(webhook_url, digest_md, f"[上游监控] {args.upstream_repo} 有新动态")

    write_summary(digest_md)
    print(f"已更新追踪 Issue #{tracking_issue['number']} 并发布摘要评论")
    return 0


if __name__ == "__main__":
    sys.exit(main())
