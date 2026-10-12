import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1] / "tools"
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from evaluate_h2o_decision_candidate import local_auth_headers, local_service_url


class H2ODecisionEvalSecurityTests(unittest.TestCase):
    def test_evaluator_requires_a_long_random_api_key(self):
        for key in (None, "short-key"):
            environment = {} if key is None else {"XIAOK_H2O_EVAL_API_KEY": key}
            with self.subTest(key_present=key is not None), patch.dict(os.environ, environment, clear=True):
                with self.assertRaisesRegex(RuntimeError, "缺少随机服务密钥"):
                    local_auth_headers()

    def test_api_key_is_sent_only_in_authorization_header(self):
        secret = "random-local-test-key-with-at-least-32-bytes"
        with patch.dict(os.environ, {"XIAOK_H2O_EVAL_API_KEY": secret}, clear=True):
            headers = local_auth_headers()
        self.assertEqual(headers["Authorization"], f"Bearer {secret}")
        self.assertNotIn(secret, str({key: value for key, value in headers.items() if key != "Authorization"}))

    def test_endpoint_always_uses_loopback_and_rejects_invalid_ports(self):
        with patch.dict(os.environ, {"XIAOK_H2O_EVAL_PORT": "56835"}, clear=True):
            self.assertEqual(local_service_url(), "http://127.0.0.1:56835")
        for port in ("0", "65536", "https://example.invalid", "9931;evil"):
            with self.subTest(port=port), patch.dict(os.environ, {"XIAOK_H2O_EVAL_PORT": port}, clear=True):
                with self.assertRaises(RuntimeError):
                    local_service_url()


if __name__ == "__main__":
    unittest.main()
