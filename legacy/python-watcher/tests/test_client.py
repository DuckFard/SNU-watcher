from __future__ import annotations

import unittest
from dataclasses import dataclass

from snu_slot_watcher.client import (
    FetchError,
    PublicSearchClient,
    RateLimited,
    TransientFetchError,
)


@dataclass
class FakeResponse:
    text: str
    url: str
    status_code: int = 200
    headers: dict[str, str] | None = None

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code}")


class FakeSession:
    def __init__(self, responses: list[FakeResponse]) -> None:
        self.responses = list(responses)
        self.calls: list[tuple[str, str, dict[str, object]]] = []
        self.headers: dict[str, str] = {}

    def get(self, url: str, **kwargs: object) -> FakeResponse:
        self.calls.append(("GET", url, kwargs))
        return self.responses.pop(0)

    def post(self, url: str, **kwargs: object) -> FakeResponse:
        self.calls.append(("POST", url, kwargs))
        return self.responses.pop(0)


class ClientTests(unittest.TestCase):
    def test_returns_every_advertised_result_page(self) -> None:
        first_page = """
        <html><title>검색결과 | 강좌검색</title>
        <body><div id='section-001'></div>
        <a href="javascript:fnGotoPage('2');">2</a></body></html>
        """
        second_page = """
        <html><title>검색결과 | 강좌검색</title>
        <body><div id='section-012'></div></body></html>
        """
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse(first_page, "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action"),
            FakeResponse(second_page, "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action"),
        ])

        delays: list[float] = []
        pages = PublicSearchClient(
            session=session,
            page_spacing_seconds=2.0,
            sleep=delays.append,
        ).search(
            2026, "U000200002", "F32.102"
        )

        self.assertEqual(len(pages), 2)
        self.assertIn("section-012", pages[1])
        self.assertEqual(delays, [2.0])

    def test_initializes_session_and_posts_exact_search_fields(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("<html><title>검색결과 | 강좌검색</title>results</html>", "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action"),
        ])
        client = PublicSearchClient(session=session)

        pages = client.search(2026, "U000200002", "4190.412")

        self.assertIn("검색결과", pages[0])
        method, _, kwargs = session.calls[1]
        self.assertEqual(method, "POST")
        self.assertEqual(kwargs["data"]["srchSbjtCd"], "4190.412")
        self.assertEqual(kwargs["data"]["srchOpenShtm"], "U000200002")

    def test_raises_rate_limit_with_retry_after(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("busy", "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action", 429, {"Retry-After": "90"}),
        ])

        with self.assertRaises(RateLimited) as raised:
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")

        self.assertEqual(raised.exception.retry_after_seconds, 90)

    def test_rejects_server_error(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("maintenance", "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action", 503, {}),
        ])

        with self.assertRaises(TransientFetchError):
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")

    def test_rejects_redirect_to_gate(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("gate", "https://sugang.snu.ac.kr/", 200, {}),
        ])

        with self.assertRaisesRegex(TransientFetchError, "redirected"):
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")

    def test_rejects_redirect_to_lookalike_search_path(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse(
                "lookalike",
                "https://example.test/sugang/cc/cc100InterfaceSrch.action",
                200,
                {},
            ),
        ])

        with self.assertRaisesRegex(TransientFetchError, "redirected"):
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")

    def test_rejects_non_retryable_http_error(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("forbidden", "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action", 403, {}),
        ])

        with self.assertRaises(FetchError):
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")

    def test_rejects_gate_content_served_at_search_url(self) -> None:
        session = FakeSession([
            FakeResponse("home", "https://sugang.snu.ac.kr/"),
            FakeResponse("<html><title>서울대학교 수강신청 프로그램</title></html>", "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action", 200, {}),
        ])

        with self.assertRaisesRegex(TransientFetchError, "gate"):
            PublicSearchClient(session=session).search(2026, "U000200002", "4190.412")


if __name__ == "__main__":
    unittest.main()
