"""Read data and assets from FFCafe's XIVAPI."""

import json
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

API = "https://xivapi-v2.xivcdn.com/api"
LANGUAGE = "chs"
PAGE_SIZE = 500


def request_data(path, params, accept):
    url = f"{API}/{path}?{urlencode(params)}"
    request = Request(url, headers={"Accept": accept, "User-Agent": "Lodestone EXD importer"})
    for attempt in range(4):
        try:
            with urlopen(request, timeout=30) as response:
                return response.read()
        except HTTPError as error:
            if error.code not in {429, 500, 502, 503, 504} or attempt == 3:
                detail = error.read(1024).decode("utf-8", errors="replace")
                raise RuntimeError(f"{path}: XIVAPI HTTP {error.code}: {detail}") from error
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
        page = json.loads(request_data(f"sheet/{sheet}", params, "application/json"))
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
