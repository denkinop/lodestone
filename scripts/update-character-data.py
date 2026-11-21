"""Refresh the compact, offline character data from FFCafe's XIVAPI."""

import gzip
import hashlib
import json
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


API = "https://xivapi-v2.xivcdn.com/api"
LANGUAGE = "chs"
PAGE_SIZE = 500
OUTPUT = Path(__file__).resolve().parents[1] / "data" / "character" / "zh-cn.json.gz"
SLOTS = (
    "MainHand", "OffHand", "Head", "Body", "Gloves", "Legs", "Feet",
    "Ears", "Neck", "Wrists", "FingerL", "FingerR",
)
ITEM_FIELDS = (
    "Name", "Icon@as(raw)", "ModelMain", "ModelSub", "EquipSlotCategory@as(raw)",
    "ItemUICategory@as(raw)", "ClassJobCategory@as(raw)", "Rarity", "LevelItem@as(raw)",
    "LevelEquip", "DamagePhys", "DamageMag", "DefensePhys", "DefenseMag",
    "BaseParam@as(raw)", "BaseParamValue",
)
SHEETS = {
    "EquipSlotCategory": SLOTS,
    "ItemUICategory": ("Name",),
    "ClassJobCategory": ("Name",),
    "BaseParam": ("Name",),
    "Stain": ("Name", "Color"),
    "World": ("Name", "IsPublic", "DataCenter.Region@as(raw)"),
    "GuardianDeity": ("Name",),
    "Item": ITEM_FIELDS,
}
MODEL_MASK = (1 << 32) - 1
WEAPON_MASK = (1 << 48) - 1


def request_page(sheet, params):
    url = f"{API}/sheet/{sheet}?{urlencode(params)}"
    request = Request(url, headers={"Accept": "application/json", "User-Agent": "Lodestone EXD importer"})
    for attempt in range(4):
        try:
            with urlopen(request, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code not in {429, 500, 502, 503, 504} or attempt == 3:
                detail = error.read(1024).decode("utf-8", errors="replace")
                raise RuntimeError(f"{sheet}: XIVAPI HTTP {error.code}: {detail}") from error
            retry_after = error.headers.get("Retry-After", "")
            delay = min(30, int(retry_after)) if retry_after.isdecimal() else 2 ** attempt
        except (URLError, TimeoutError):
            if attempt == 3:
                raise
            delay = 2 ** attempt
        time.sleep(delay)


def read_sheet(sheet, fields, release):
    params = {"language": LANGUAGE, "fields": ",".join(fields), "limit": PAGE_SIZE}
    rows = {}
    previous = -1
    while True:
        if release:
            params["schema"] = release["schema"]
        page = request_page(sheet, params)
        version = {"version": page["version"], "schema": page["schema"]}
        if release and version != release:
            raise RuntimeError(f"{sheet}: XIVAPI changed releases during import")
        release = version
        if not page["rows"]:
            if not rows:
                raise RuntimeError(f"{sheet}: XIVAPI returned no rows")
            return rows, release
        for row in page["rows"]:
            row_id = row["row_id"]
            if type(row_id) is not int or row_id <= previous:
                raise RuntimeError(f"{sheet}: invalid page after row {previous}")
            rows[row_id] = row["fields"]
            previous = row_id
        params["after"] = previous


def model_keys(item, slots):
    main = item["ModelMain"]
    sub = item["ModelSub"] & WEAPON_MASK
    models = []
    for index, slot in enumerate(SLOTS):
        if slots[slot] <= 0:
            continue
        if index == 1 and slots["MainHand"] > 0:
            continue
        model = main & (WEAPON_MASK if index < 2 else MODEL_MASK)
        if model & 0xffff:
            models.append([index, model])
    # Paired weapons use ModelSub even when the slot category blocks equipping a separate off hand.
    if slots["MainHand"] > 0 and sub & 0xffff:
        models.append([1, sub])
    return models


def compact_item(row_id, item, tables):
    name = item["Name"].strip()
    if not name or not (item["ModelMain"] or item["ModelSub"]):
        return None
    models = model_keys(item, tables["EquipSlotCategory"][item["EquipSlotCategory@as(raw)"]])
    if not models:
        return None
    attributes = []
    for param, value in zip(item["BaseParam@as(raw)"], item["BaseParamValue"], strict=True):
        if param and value:
            label = tables["BaseParam"][param]["Name"].strip()
            if label:
                attributes.append([label, value])
    details = [
        tables["ItemUICategory"][item["ItemUICategory@as(raw)"]]["Name"].strip(),
        tables["ClassJobCategory"][item["ClassJobCategory@as(raw)"]]["Name"].strip(),
        item["Rarity"], item["LevelItem@as(raw)"], item["LevelEquip"],
        item["DamagePhys"], item["DamageMag"], item["DefensePhys"], item["DefenseMag"], attributes,
    ]
    return [row_id, name, item["Icon@as(raw)"], models, details]


def world_names(rows):
    worlds = []
    for row_id, fields in rows.items():
        name = fields["Name"].strip()
        center = fields["DataCenter"].get("fields") or {}
        region = center.get("Region@as(raw)")
        # The merged table includes retired CN and Korean world IDs without a datacenter.
        regional_name = any("\u3400" <= char <= "\u9fff" or "\uac00" <= char <= "\ud7af" for char in name)
        if 0 < row_id < 65535 and name and (fields["IsPublic"] or region in (5, 6) or regional_name):
            worlds.append([row_id, name])
    return worlds


def compact_json(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def main():
    tables = {}
    release = None
    for sheet, fields in SHEETS.items():
        tables[sheet], release = read_sheet(sheet, fields, release)
        print(f"{sheet}: {len(tables[sheet])} XIVAPI rows", flush=True)

    data = {
        "source": {"api": API, "language": LANGUAGE, **release},
        "sources": {sheet: hashlib.sha256(compact_json(rows)).hexdigest() for sheet, rows in tables.items()},
        "items": [item for row_id, fields in tables["Item"].items()
                  if (item := compact_item(row_id, fields, tables)) is not None],
        "stains": [[row_id, fields["Name"].strip() or f"染色 {row_id}", f'#{fields["Color"] & 0xffffff:06x}']
                   for row_id, fields in tables["Stain"].items() if row_id > 0],
        "worlds": world_names(tables["World"]),
        "guardianDeities": [[row_id, fields["Name"].strip()]
                            for row_id, fields in tables["GuardianDeity"].items() if row_id and fields["Name"].strip()],
    }
    if not all(data[key] for key in ("items", "stains", "worlds", "guardianDeities")):
        raise RuntimeError("Refusing to replace the character data with an empty table")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=OUTPUT.parent, delete=False) as file:
            temporary = Path(file.name)
            file.write(gzip.compress(compact_json(data), mtime=0))
        temporary.replace(OUTPUT)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    print(f"{len(data['items'])} equipment records saved to {OUTPUT}")


if __name__ == "__main__":
    main()
