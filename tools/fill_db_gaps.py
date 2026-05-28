"""
fill_db_gaps.py
---------------
Nexus DB 갭 채우기 스크립트.

누락된 modId에 대해 Nexus API를 호출하고,
BG3 pak 파일이 있는 모드를 DB에 추가합니다.

사용법:
  python fill_db_gaps.py --api-key YOUR_KEY --db nexusid_db.json

옵션:
  --api-key   Nexus API 키 (필수)
  --db        DB 파일 경로 (기본: nexusid_db.json)
  --dry-run   DB에 실제로 저장하지 않고 결과만 출력
  --delay     API 호출 간격(초), 기본 0.15
"""

import json
import time
import argparse
import os
import sys
import requests

GAME_DOMAIN = "baldursgate3"
BASE_URL    = "https://api.nexusmods.com"
PROGRESS_FILE = "fill_db_gaps_progress.json"


def make_headers(api_key: str) -> dict:
    return {
        "apikey":     api_key,
        "User-Agent": "BG3ModHelper/1.0 (db-gap-filler)",
        "Accept":     "application/json",
    }


def get_json(url: str, headers: dict, delay: float) -> tuple[dict | None, int]:
    """GET 요청. (data, status_code) 반환. 실패 시 data=None."""
    try:
        resp = requests.get(url, headers=headers, timeout=15)
        time.sleep(delay)
        if resp.status_code in (404, 403):
            return None, resp.status_code
        if not resp.ok:
            print(f"  HTTP {resp.status_code}: {url}")
            return None, resp.status_code
        return resp.json(), 200
    except Exception as e:
        print(f"  요청 실패: {e}")
        return None, 0


def collect_pak_names(children: list) -> list[str]:
    """children 트리를 재귀 탐색해 .pak 파일명 수집."""
    result = []
    for node in children:
        name = node.get("name", "")
        if isinstance(name, str) and name.lower().endswith(".pak"):
            result.append(name)
        elif "children" in node:
            result.extend(collect_pak_names(node["children"]))
    return result


def get_pak_names(preview_url: str) -> list[str]:
    """content_preview_link에서 .pak 파일명 추출 (인증 불필요, 폴더 중첩 지원)."""
    try:
        resp = requests.get(preview_url, timeout=10)
        if not resp.ok:
            return []
        children = resp.json().get("children", [])
        return collect_pak_names(children)
    except Exception:
        return []


def pick_primary_file(files: list) -> dict | None:
    """MAIN 또는 가장 최근 파일 선택 (앱의 GetPakNamesAsync 로직과 동일)."""
    if not files:
        return None
    primary = next((f for f in files if f.get("is_primary")), None)
    if primary:
        return primary
    main = next((f for f in files
                 if str(f.get("category_name", "")).upper() == "MAIN"), None)
    if main:
        return main
    return max(files, key=lambda f: f.get("uploaded_timestamp", 0), default=None)


def load_progress() -> set:
    if not os.path.exists(PROGRESS_FILE):
        return set()
    try:
        with open(PROGRESS_FILE, encoding="utf-8") as f:
            return set(json.load(f).get("checked", []))
    except Exception:
        return set()


def save_progress(checked: set):
    with open(PROGRESS_FILE, "w", encoding="utf-8") as f:
        json.dump({"checked": sorted(checked)}, f)


def load_db(path: str) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def format_entry(mod_id: str, entry: dict) -> str:
    """update_db.py와 동일한 포맷으로 항목 직렬화."""
    paks = ",\n".join(
        json.dumps(p, ensure_ascii=False, separators=(",", ":"))
        for p in entry["paks"]
    )
    return (
        f'"{mod_id}":'
        f'{{"nexusModName":{json.dumps(entry["nexusModName"], ensure_ascii=False)},'
        f'"nexusUploadedBy":{json.dumps(entry.get("nexusUploadedBy",""), ensure_ascii=False)},'
        f'"nexusModId":{entry["nexusModId"]},'
        f'"paks":[\n{paks}\n]}}'
    )


def save_db(db: dict, path: str):
    """update_db.py의 save_db와 동일한 포맷으로 저장."""
    meta = db.get("_meta", {})
    meta_str = json.dumps(meta, ensure_ascii=False, separators=(",", ":"))
    sorted_items = sorted(
        ((k, v) for k, v in db.items() if k != "_meta"),
        key=lambda kv: int(kv[0])
    )
    entries = ",\n".join(format_entry(k, v) for k, v in sorted_items)
    with open(path, "w", encoding="utf-8") as f:
        f.write('{"_meta":' + meta_str + ",\n" + entries + "}")
    print(f"  DB 저장 완료: {path}")


def find_gaps(db: dict) -> list[int]:
    ids = sorted(int(k) for k in db.keys() if k.isdigit())
    if not ids:
        return []
    max_id = max(ids)
    id_set = set(ids)
    return [i for i in range(1, max_id + 1) if i not in id_set]


def main():
    parser = argparse.ArgumentParser(description="Nexus DB 갭 채우기")
    parser.add_argument("--api-key", required=True,  help="Nexus API 키")
    parser.add_argument("--db",      required=True,  help="DB JSON 파일 경로")
    parser.add_argument("--dry-run", action="store_true", help="저장하지 않고 출력만")
    parser.add_argument("--delay",   type=float, default=0.15, help="API 호출 간격(초)")
    args = parser.parse_args()

    if not os.path.exists(args.db):
        print(f"DB 파일 없음: {args.db}")
        sys.exit(1)

    headers = make_headers(args.api_key)
    db      = load_db(args.db)
    gaps    = find_gaps(db)
    checked = load_progress()

    remaining = [g for g in gaps if g not in checked]
    print(f"전체 갭: {len(gaps)}개 / 미처리: {len(remaining)}개")
    if not remaining:
        print("처리할 갭이 없습니다.")
        return

    added   = 0
    skipped = 0
    save_interval = 50  # N개마다 DB 저장

    for idx, mod_id in enumerate(remaining, 1):
        print(f"[{idx}/{len(remaining)}] mod {mod_id} 확인 중...", end=" ", flush=True)

        # 1) 모드 정보
        mod_url  = f"{BASE_URL}/v1/games/{GAME_DOMAIN}/mods/{mod_id}.json"
        mod_data, status = get_json(mod_url, headers, args.delay)
        if mod_data is None:
            print(f"없음 ({status})")
            checked.add(mod_id)
            skipped += 1
            continue

        mod_name    = mod_data.get("name", "")
        uploaded_by = mod_data.get("uploaded_by", "")

        # 2) 파일 목록
        files_url  = f"{BASE_URL}/v1/games/{GAME_DOMAIN}/mods/{mod_id}/files.json"
        files_data, _ = get_json(files_url, headers, args.delay)
        if not files_data or "files" not in files_data:
            print("파일 없음")
            checked.add(mod_id)
            skipped += 1
            continue

        # 3) pak 파일 추출 (primary 파일에서만)
        target_file = pick_primary_file(files_data["files"])
        if not target_file:
            print("대상 파일 없음")
            checked.add(mod_id)
            skipped += 1
            continue

        preview_url = target_file.get("content_preview_link", "")
        pak_names   = get_pak_names(preview_url) if preview_url else []

        if not pak_names:
            print(f"pak 없음 [{mod_name}]")
            checked.add(mod_id)
            skipped += 1
            continue

        # 4) DB 항목 구성
        paks = [
            {
                "nexusFileName":    target_file.get("name", ""),
                "nexusFileVersion": target_file.get("version", "") or "",
                "pakFileName":      pak_name,
                "nexusFileId":      target_file.get("file_id", 0),
                "metaUuid":         None,
            }
            for pak_name in pak_names
        ]

        print(f"추가! [{mod_name}] — pak {len(paks)}개")

        if not args.dry_run:
            db[str(mod_id)] = {
                "nexusModName":   mod_name,
                "nexusUploadedBy": uploaded_by,
                "nexusModId":     mod_id,
                "paks":           paks,
            }

        checked.add(mod_id)
        added += 1

        # 주기적 저장
        if not args.dry_run and added % save_interval == 0:
            save_db(db, args.db)
            save_progress(checked)

    # 최종 저장
    if not args.dry_run:
        save_db(db, args.db)
    save_progress(checked)

    print(f"\n완료 — 추가: {added}개 / 스킵: {skipped}개")


if __name__ == "__main__":
    main()
