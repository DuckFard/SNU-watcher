from __future__ import annotations

from dataclasses import dataclass
import re
import time
from typing import Callable, Protocol
from urllib.parse import urlparse

import requests


BASE_URL = "https://sugang.snu.ac.kr"
HOME_URL = f"{BASE_URL}/sugang/co/co010.action"
SEARCH_URL = f"{BASE_URL}/sugang/cc/cc100InterfaceSrch.action"
SEARCH_PATH = "/sugang/cc/cc100InterfaceSrch.action"
USER_AGENT = "SNU-Slot-Watcher/0.1 (read-only public capacity monitor)"
TITLE_PATTERN = re.compile(r"<title[^>]*>(.*?)</title>", re.IGNORECASE | re.DOTALL)
PAGE_LINK_PATTERN = re.compile(
    r"fnGotoPage\(\s*['\"]?(\d+)['\"]?(?:\s*,[^)]*)?\s*\)"
)
MAX_SEARCH_PAGES = 25


class FetchError(RuntimeError):
    """Base error for public-search requests."""


@dataclass(frozen=True, slots=True)
class RateLimited(FetchError):
    retry_after_seconds: int | None

    def __str__(self) -> str:
        return "SNU public search is rate-limiting requests"


class TransientFetchError(FetchError):
    """Raised for a temporary network, maintenance, or server error."""


class ResponseLike(Protocol):
    text: str
    url: str
    status_code: int
    headers: dict[str, str]

    def raise_for_status(self) -> None: ...


class SessionLike(Protocol):
    headers: dict[str, str]

    def get(self, url: str, **kwargs: object) -> ResponseLike: ...

    def post(self, url: str, **kwargs: object) -> ResponseLike: ...


class PublicSearchClient:
    def __init__(
        self,
        *,
        session: SessionLike | None = None,
        timeout_seconds: float = 15.0,
        page_spacing_seconds: float = 2.0,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self._session = session or requests.Session()
        self._session.headers = {
            **dict(self._session.headers),
            "User-Agent": USER_AGENT,
            "Accept-Language": "ko,en;q=0.8",
        }
        self._timeout_seconds = timeout_seconds
        self._page_spacing_seconds = max(0.0, page_spacing_seconds)
        self._sleep = sleep
        self._referer: str | None = None

    def _initialize(self) -> None:
        try:
            response = self._session.get(
                HOME_URL,
                timeout=self._timeout_seconds,
                allow_redirects=True,
            )
            response.raise_for_status()
        except requests.RequestException as error:
            raise TransientFetchError("Could not initialize public SNU search") from error
        except RuntimeError as error:
            raise TransientFetchError("Could not initialize public SNU search") from error
        self._referer = response.url

    @staticmethod
    def _retry_after(response: ResponseLike) -> int | None:
        headers = response.headers or {}
        raw_value = headers.get("Retry-After")
        if raw_value is None:
            return None
        try:
            return max(0, int(raw_value))
        except ValueError:
            return None

    def _post_search(
        self,
        year: int,
        semester_code: str,
        course_code: str,
        page_number: int = 1,
    ) -> ResponseLike:
        payload = {
            "workType": "S",
            "pageNo": str(page_number),
            "srchOpenSchyy": str(year),
            "srchOpenShtm": semester_code,
            "srchSbjtNm": "",
            "srchSbjtCd": course_code,
            "srchCurrPage": str(page_number),
            "srchPageSize": "100",
        }
        try:
            return self._session.post(
                SEARCH_URL,
                data=payload,
                headers={"Referer": self._referer or BASE_URL},
                timeout=self._timeout_seconds,
                allow_redirects=True,
            )
        except requests.RequestException as error:
            raise TransientFetchError("Public SNU search request failed") from error

    def _validate_search_response(self, response: ResponseLike) -> str:
        if response.status_code == 429:
            raise RateLimited(self._retry_after(response))
        if response.status_code >= 500:
            raise TransientFetchError(
                f"SNU public search returned HTTP {response.status_code}"
            )
        try:
            response.raise_for_status()
        except requests.RequestException as error:
            raise FetchError(
                f"SNU public search returned HTTP {response.status_code}"
            ) from error
        except RuntimeError as error:
            raise FetchError(
                f"SNU public search returned HTTP {response.status_code}"
            ) from error

        final_url = urlparse(response.url)
        if (
            final_url.scheme != "https"
            or final_url.hostname != "sugang.snu.ac.kr"
            or final_url.path != SEARCH_PATH
        ):
            self._referer = None
            raise TransientFetchError(
                "SNU redirected the public search to a gate or maintenance page"
            )
        title_match = TITLE_PATTERN.search(response.text)
        title = title_match.group(1) if title_match else ""
        if "검색결과" not in title and "Search Results" not in title:
            raise TransientFetchError(
                "SNU served a gate or maintenance page instead of search results"
            )
        return response.text

    @staticmethod
    def _linked_page_numbers(html: str) -> tuple[int, ...]:
        page_numbers = tuple(
            sorted({int(value) for value in PAGE_LINK_PATTERN.findall(html)})
        )
        if any(number < 1 or number > MAX_SEARCH_PAGES for number in page_numbers):
            raise TransientFetchError(
                "SNU search advertised an unexpected number of result pages"
            )
        return page_numbers

    def search(
        self, year: int, semester_code: str, course_code: str
    ) -> tuple[str, ...]:
        if self._referer is None:
            self._initialize()

        pending = [1]
        fetched: dict[int, str] = {}
        while pending:
            page_number = pending.pop(0)
            if fetched and self._page_spacing_seconds > 0:
                self._sleep(self._page_spacing_seconds)
            response = self._post_search(
                year, semester_code, course_code, page_number
            )
            html = self._validate_search_response(response)
            fetched = {**fetched, page_number: html}
            self._referer = response.url

            discovered = tuple(
                number
                for number in self._linked_page_numbers(html)
                if number not in fetched and number not in pending
            )
            pending = sorted((*pending, *discovered))

        return tuple(fetched[number] for number in sorted(fetched))
