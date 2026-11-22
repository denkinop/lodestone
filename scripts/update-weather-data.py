"""Refresh the Chinese weather catalog and icons bundled with the weather page."""

import json
from pathlib import Path
from tempfile import TemporaryDirectory

from xivapi import API, LANGUAGE, read_sheet, request_data

FRONTEND = Path(__file__).resolve().parents[1] / "frontend"
SHEETS = {
    "TerritoryType": ("Name", "Map@as(raw)", "PlaceName.Name", "PlaceNameRegion.Name",
                      "TerritoryIntendedUse@as(raw)", "WeatherRate@as(raw)"),
    "WeatherRate": ("Rate", "Weather@as(raw)"),
    "Weather": ("Name", "Icon"),
}
# Towns, open fields, housing, and exploration areas with predictable weather.
MAP_TYPES = {0, 1, 13, 21, 41, 48, 49, 56, 60, 61}
SPECIAL_REGIONS = {41: ("eureka", "优雷卡"), 48: ("bozja", "博兹雅"),
                   49: ("island", "无人岛开拓"), 61: ("crescent", "新月岛")}
CURRENT_DIADEM = 656


def weather_catalog(tables):
    territories, rates, weather = (tables[sheet] for sheet in SHEETS)
    regions = {}
    used_weather = set()
    for territory_id, territory in territories.items():
        use = territory["TerritoryIntendedUse@as(raw)"]
        if (use not in MAP_TYPES and territory_id != CURRENT_DIADEM) or territory["Name"].endswith("_test"):
            continue
        rate_id = territory["WeatherRate@as(raw)"]
        name = territory["PlaceName"].get("fields", {}).get("Name", "").strip()
        if not rate_id or not name or not territory["Map@as(raw)"]:
            continue
        place = territory["PlaceNameRegion"]
        region_id, region_name = SPECIAL_REGIONS.get(use, (str(place["value"]), place.get("fields", {}).get("Name", "")))
        if not region_name:
            raise RuntimeError(f"Missing region for {name} ({territory_id})")
        amounts = rates[rate_id]
        distribution = []
        upper = 0
        for weather_id, chance in zip(amounts["Weather@as(raw)"], amounts["Rate"], strict=True):
            if not chance:
                continue
            if not weather_id or not 0 < chance <= 100 or not weather[weather_id]["Name"].strip():
                raise RuntimeError(f"Invalid weather entry in WeatherRate/{rate_id}")
            upper += chance
            distribution.append({"weather": weather_id, "upper": upper})
            used_weather.add(weather_id)
        if not distribution or upper > 100:
            raise RuntimeError(f"Invalid total in WeatherRate/{rate_id}")
        # The final weather covers any remaining rolls, including Empyreum's 90% table.
        distribution[-1]["upper"] = 100
        region = regions.setdefault(region_id, {"id": region_id, "name": region_name, "maps": []})
        region["maps"].append({"id": territory_id, "name": name, "rates": distribution})
    return list(regions.values()), {row_id: weather[row_id] for row_id in sorted(used_weather)}


def main():
    tables = {}
    release = None
    for sheet, fields in SHEETS.items():
        tables[sheet], release = read_sheet(sheet, fields, release)
        print(f"{sheet}: {len(tables[sheet])} XIVAPI rows", flush=True)
    regions, weather = weather_catalog(tables)
    if not regions or not weather:
        raise RuntimeError("XIVAPI returned an empty weather catalog")

    icons = FRONTEND / "public" / "images" / "weather"
    output = FRONTEND / "src" / "weather-data.json"
    data = {"source": {"api": API, "language": LANGUAGE, **release}, "regions": regions, "weather": {}}
    with TemporaryDirectory(dir=FRONTEND) as folder:
        staging = Path(folder)
        for row_id, fields in weather.items():
            icon = fields["Icon"]
            filename = f'{icon["id"]}.webp'
            if not (staging / filename).is_file():
                content = request_data("asset", {"path": icon["path_hr1"], "format": "webp"}, "image/webp")
                if content[:4] != b"RIFF" or content[8:12] != b"WEBP":
                    raise RuntimeError(f"XIVAPI returned an invalid image for Weather/{row_id}")
                (staging / filename).write_bytes(content)
            data["weather"][row_id] = {"name": fields["Name"].strip(), "icon": filename}
        (staging / output.name).write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        icons.mkdir(exist_ok=True)
        for image in staging.glob("*.webp"):
            image.replace(icons / image.name)
        (staging / output.name).replace(output)
    print(f"{sum(len(region['maps']) for region in regions)} maps in {len(regions)} regions")


if __name__ == "__main__":
    main()
