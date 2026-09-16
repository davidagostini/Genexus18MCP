import contextlib
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "bench", Path(__file__).resolve().parents[1] / "bench-live-http.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class BenchmarkGateTests(unittest.TestCase):
    def run_main(self, measured, extra=None, operation="whoami"):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "result.json"
            def http_post(payload, session_id=None, timeout=180):
                return bench.HttpResponse(200, {"MCP-Session-Id": "test-session"}, b"{}")
            calls = 0

            def rpc(session, method, params, **kwargs):
                nonlocal calls
                calls += 1
                if calls == 1:
                    return 1, None  # initialized notification
                if calls == 2:
                    return 1, {"status": "ok"}
                if calls == 3:
                    return 1, {"status": "ok", "index": {"status": "Ready"}}
                if calls == 4:
                    return 1, {"status": "ok", "results": [{"name": "Probe"}]}
                return 10, measured

            argv = ["bench", "--ops", operation, "--iterations", "1", "--out", str(output)]
            with patch.object(bench.sys, "argv", argv + (extra or [])), \
                    patch.object(bench, "rpc", rpc), \
                    patch.object(bench, "http_post", http_post), \
                    patch.object(bench.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
                result = bench.main()
            return result, json.loads(output.read_text()) if output.exists() else None

    def test_errors_cannot_be_successful_latency_samples(self):
        for envelope in (None, {}, {"error": {"code": -32603}},
                         {"isError": True, "status": "ok"}, {"status": "error"}):
            with self.subTest(envelope=envelope):
                code, report = self.run_main(envelope)
                self.assertNotEqual(0, code)
                self.assertEqual(0, report["ops"]["whoami"]["n"])
                self.assertEqual(1, report["ops"]["whoami"]["failed"])

    def test_success_counts_and_samples(self):
        code, report = self.run_main({"connected": True, "kb": {}})
        self.assertEqual(0, code)
        self.assertEqual(1, report["ops"]["whoami"]["succeeded"])
        self.assertEqual([10], report["ops"]["whoami"]["samples"])
        self.assertEqual(0, report["ops"]["whoami"]["responseBytes"]["n"])
        self.assertIn("population", report)

    def test_empty_collection_is_a_valid_result(self):
        code, report = self.run_main({"status": "ok", "results": []}, operation="list_objects")
        self.assertEqual(0, code)
        self.assertEqual(1, report["ops"]["list_objects"]["succeeded"])

    def test_statusless_gateway_shapes_are_valid_for_live_read_operations(self):
        cases = (
            ("whoami", {"connected": True, "kb": {"name": "KBTeste"}}),
            ("list_objects", {"results": []}),
            ("query", {"results": []}),
            ("search_source", {"status": "ok", "result": {"hits": []}}),
            ("inspect", {"name": "Probe", "type": "Procedure"}),
            ("read", {"part": "Source", "source": "parm;"}),
            ("lifecycle_status", {"Status": "Ready", "Phase": "idle"}),
        )
        for operation, envelope in cases:
            with self.subTest(operation=operation):
                self.assertTrue(bench.operation_envelope_is_ok(operation, envelope))

    def test_statusless_success_without_requested_shape_is_rejected(self):
        for operation in ("whoami", "inspect", "read", "lifecycle_status",
                          "graph", "design_system", "pattern_diagnose"):
            with self.subTest(operation=operation):
                self.assertFalse(bench.operation_envelope_is_ok(operation, {"message": "unknown"}))

    def test_graph_design_and_pattern_success_shapes_are_validated(self):
        cases = (
            ("graph", {"nodes": [], "edges": []}),
            ("design_system", {"tokens": {}, "classes": []}),
            ("pattern_diagnose", {"findings": [], "pattern": "WorkWithPlus"}),
        )
        for operation, envelope in cases:
            with self.subTest(operation=operation):
                self.assertTrue(bench.operation_envelope_is_ok(operation, envelope))

    def test_bounded_operation_catalog_contains_lifecycle_and_extended_families(self):
        self.assertLessEqual(bench.MAX_ITERATIONS, 20)
        for operation in ("whoami", "kb_list", "list_objects", "query", "search_source",
                          "inspect", "read", "lifecycle_status", "pattern_diagnose"):
            self.assertIn(operation, bench.DEFAULT_OPS)
        for operation in ("kb_list", "kb_select", "graph", "design_system", "pattern_diagnose"):
            self.assertIn(operation, bench.ALL_OPS)

    def test_extended_operations_record_only_validated_successes(self):
        cases = {
            "kb_list": {"items": []},
            "kb_select": {"selected": "live", "selectionState": "valid"},
            "graph": {"nodes": [], "edges": []},
            "design_system": {"tokens": {}, "classes": []},
            "pattern_diagnose": {"findings": [], "pattern": "WorkWithPlus"},
        }
        for operation, envelope in cases.items():
            with self.subTest(operation=operation):
                code, report = self.run_main(envelope, operation=operation)
                self.assertEqual(0, code)
                self.assertEqual(1, report["ops"][operation]["succeeded"])
                self.assertEqual(0, report["ops"][operation]["failed"])

    def test_error_status_with_success_shape_is_rejected(self):
        self.assertFalse(bench.operation_envelope_is_ok(
            "list_objects", {"status": "InvalidArgs", "results": []}))

    def test_read_target_selection_prefers_source_backed_types(self):
        targets = bench.select_read_targets([
            {"name": "Root", "type": "Folder"},
            {"name": "Client", "type": "Module"},
            {"name": "Customer", "type": "Transaction"},
            {"name": "Customer", "type": "Table"},
        ])
        self.assertEqual([{"name": "Customer", "type": "Transaction"}], targets)

    def test_success_without_requested_collection_is_a_failure(self):
        code, report = self.run_main({"status": "ok"}, operation="query")
        self.assertEqual(1, code)
        self.assertEqual(1, report["ops"]["query"]["failed"])

    def test_missing_baseline_fails_gate(self):
        code, _ = self.run_main({"status": "ok"}, ["--compare", "missing-baseline.json", "--fail-on-regression"])
        self.assertNotEqual(0, code)

    def test_missing_operation_invalidates_comparison(self):
        stats = {"n": 1, "p50": 10, "p95": 10}
        with contextlib.redirect_stdout(io.StringIO()):
            result = bench.print_comparison(
                {"ops": {"whoami": stats, "read": stats}},
                {"ops": {"whoami": stats}}, 25)
        self.assertIsNone(result)

    def serving(self, body, status=200):
        """Patch the transport with a canned response body (or HTTP status)."""
        return patch.object(bench, "http_post",
                            lambda payload, session_id=None, timeout=180:
                            bench.HttpResponse(status, {}, body))

    def test_rpc_preserves_outer_error_even_with_successful_text(self):
        for outer in (
                {"error": {"code": -32603}},
                {"result": {"isError": True, "content": [{"text": '{"status":"ok"}'}]}}):
            with self.serving(json.dumps(outer).encode()):
                _, envelope = bench.rpc("s", "tools/call", {})
            self.assertFalse(bench.envelope_is_ok(envelope))

    def test_rpc_accepts_structured_content(self):
        with self.serving(b'{"result":{"structuredContent":{"status":"ok"}}}'):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertGreater(measurement.response_bytes, 0)

    def test_rpc_reports_an_http_error_status_as_an_envelope(self):
        with self.serving(b"backend unavailable", status=503):
            measurement = bench.rpc("s", "tools/call", {})
            _, envelope = measurement
        self.assertEqual({"__http_error__": 503}, envelope)
        self.assertFalse(bench.envelope_is_ok(envelope))
        self.assertEqual(503, measurement.status_code)

    class FakeConnection:
        """Stands in for http.client.HTTPConnection so pooling is observable."""

        created = []
        fail_first_request = False

        def __init__(self, host, port, timeout=None):
            type(self).created.append((host, port))
            self.sock = None
            self.timeout = timeout
            self.requests = []

        def request(self, method, path, body=None, headers=None):
            if type(self).fail_first_request and len(type(self).created) == 1:
                raise http.client.RemoteDisconnected("server closed the idle socket")
            self.requests.append((method, path, headers))

        def getresponse(self):
            return type(self).FakeResponse()

        def close(self):
            pass

        class FakeResponse:
            status = 200
            headers = {"MCP-Session-Id": "pooled"}

            @staticmethod
            def read():
                # A full JSON-RPC envelope, exactly like the gateway's HTTP response.
                inner = '{"status":"ok","connected":true,"kb":{}}'
                return json.dumps({"jsonrpc": "2.0", "id": 1,
                                   "result": {"content": [{"type": "text", "text": inner}]}}).encode()

    def use_fake_connections(self, fail_first=False):
        self.FakeConnection.created = []
        self.FakeConnection.fail_first_request = fail_first
        bench._close_connection()
        self.addCleanup(bench._close_connection)
        return patch.object(bench.http.client, "HTTPConnection", self.FakeConnection)

    def test_rpc_reuses_one_persistent_connection(self):
        with self.use_fake_connections():
            for _ in range(3):
                _, envelope = bench.rpc("s", "tools/call", {})
                self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertEqual(1, len(self.FakeConnection.created),
                        "a connection per call re-introduces the select() timer tax")

    def test_rpc_reconnects_once_after_a_dropped_connection(self):
        with self.use_fake_connections(fail_first=True):
            _, envelope = bench.rpc("s", "tools/call", {})
        self.assertTrue(bench.envelope_is_ok(envelope))
        self.assertEqual(2, len(self.FakeConnection.created))

    def test_rpc_surfaces_a_persistent_transport_failure(self):
        class AlwaysFailing(self.FakeConnection):
            def request(self, method, path, body=None, headers=None):
                raise OSError("connection refused")

        AlwaysFailing.created = []
        bench._close_connection()
        self.addCleanup(bench._close_connection)
        with patch.object(bench.http.client, "HTTPConnection", AlwaysFailing):
            with self.assertRaises(OSError):
                bench.rpc("s", "tools/call", {})
        self.assertEqual(2, len(AlwaysFailing.created),
                         "a failed retry must surface the error instead of looping")

    def test_skipped_dry_run_fails(self):
        code, report = self.run_main({"status": "error"}, operation="edit_dryrun")
        self.assertEqual(1, code)
        self.assertEqual(1, report["ops"]["edit_dryrun"]["skipped"])

    def test_invalid_baseline_metrics_fail(self):
        for stats in ({}, {"n": 0, "p50": 1, "p95": 1},
                      {"n": 1, "p50": float("nan"), "p95": 1},
                      {"n": 1, "p50": 1, "p95": 1, "failed": 1}):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertIsNone(bench.print_comparison(
                    {"ops": {"whoami": stats}}, {"ops": {"whoami": stats}}, 25))

    def test_tail_latency_regression_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline = Path(directory) / "baseline.json"
            baseline.write_text(json.dumps({"ops": {"whoami": {"n": 1, "p50": 5, "p95": 5}}}))
            code, _ = self.run_main({"connected": True, "kb": {}},
                                    ["--compare", str(baseline), "--fail-on-regression", "--max-p50-regression", "200"])
        # The baseline intentionally predates the population/byte contract, so
        # fail-on-regression rejects it as an invalid comparison (exit 2).
        self.assertEqual(2, code)

    def test_comparison_requires_matching_population_and_payload_metrics(self):
        population = {
            "fixtureId": "fixture-r1",
            "fixtureRevision": "seed-1",
            "kbAlias": "live",
            "kbPath": "C:/fixture",
            "generator": "net",
            "cacheMode": "warm",
            "concurrency": 1,
            "iterations": 2,
            "ops": ["whoami"],
        }
        valid = {
            "population": population,
            "ops": {
                "whoami": {
                    "n": 2, "p50": 10, "p95": 12,
                    "responseBytes": {"n": 2, "p50": 100, "p95": 110},
                    "failed": 0, "skipped": 0,
                }
            },
        }
        current = {
            "population": dict(population),
            "ops": {
                "whoami": {
                    "n": 2, "p50": 11, "p95": 13,
                    "responseBytes": {"n": 2, "p50": 101, "p95": 112},
                    "failed": 0, "skipped": 0,
                }
            },
        }
        self.assertEqual([], bench.print_comparison(valid, current, 25, 25, 25))
        current["population"]["cacheMode"] = "cold"
        self.assertIsNone(bench.print_comparison(valid, current, 25, 25, 25))


if __name__ == "__main__":
    unittest.main()
