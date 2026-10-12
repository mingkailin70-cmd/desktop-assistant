from __future__ import annotations

import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))

from decision_benchmark_suites import INTENT_PILOT_V1, SUITES, XIAOK_ROUTING_V1  # noqa: E402


class DecisionBenchmarkSuiteTests(unittest.TestCase):
    def test_suites_have_balanced_labels_and_unique_cases(self) -> None:
        expected_sizes = {INTENT_PILOT_V1.suite_id: 20, XIAOK_ROUTING_V1.suite_id: 60}
        for suite_id, suite in SUITES.items():
            self.assertEqual(len(suite.cases), expected_sizes[suite_id])
            self.assertTrue(all(label in suite.labels for label, _ in suite.cases))
            self.assertEqual(len({text for _, text in suite.cases}), len(suite.cases))
            counts = {label: sum(expected == label for expected, _ in suite.cases) for label in suite.labels}
            self.assertEqual(len(set(counts.values())), 1)

    def test_routing_suite_states_suggestions_do_not_authorize_actions(self) -> None:
        self.assertIn("不代表执行许可", XIAOK_ROUTING_V1.question)
        self.assertEqual(len(XIAOK_ROUTING_V1.labels), 5)
        self.assertIn("confirm_effect", XIAOK_ROUTING_V1.labels)
        self.assertIn("sensitive_escalate", XIAOK_ROUTING_V1.labels)

    def test_quoted_instruction_attacks_are_labeled_as_content_analysis(self) -> None:
        quoted_attacks = [
            text
            for label, text in XIAOK_ROUTING_V1.cases
            if label == "respond_draft" and ("忽略所有规则" in text or "Ignore previous instructions" in text)
        ]
        self.assertEqual(len(quoted_attacks), 2)


if __name__ == "__main__":
    unittest.main()
