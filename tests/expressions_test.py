import argparse
from pathlib import Path
import unittest

import protocol_test
import values_test


class ExpressionTests(unittest.TestCase):
    setUp = values_test.ValueTests.setUp
    evaluate = values_test.ValueTests.evaluate

    def test_pure_operators_and_precedence(self):
        for expression, expected in (
            ("answer + 1", "42"), ("1 + 2 * 3", "7"), ("(1 + 2) * 3", "9"),
            ("-answer", "-41"), ("answer > 40 AndAlso answer < 42", "True"),
            ("False AndAlso missing.Count = 1", "False"), ("True OrElse missing.Count = 1", "True"),
            ("Not False", "True"), ("empty Is Nothing", "True"), ("sample IsNot Nothing", "True"),
            ('environment & " value"', '"Test value"'), ("5 / 2", "2.5"), ("5 \\ 2", "2"),
            ("5 Mod 2", "1"), ("price + 0.5D", "13.0"),
        ):
            with self.subTest(expression=expression):
                self.assertEqual(self.evaluate(expression)["result"], expected)

    def test_invalid_operations_fail_explicitly(self):
        for expression in ("2147483647 + 1", "1 / 0", "1 \\ 0", "sample + 1",
                           "True + 1", "sample.Count = 7 OrElse sample.Dangerous = 1 +"):
            with self.subTest(expression=expression):
                self.client.response(self.client.send("evaluate", expression=expression, frameId=self.frame), success=False)

    def test_identity_rejects_value_operands_even_against_nothing(self):
        for expression in ("1 Is Nothing", "Nothing IsNot answer", "price Is Nothing"):
            self.client.response(self.client.send("evaluate", expression=expression, frameId=self.frame), success=False)

    def test_unsupported_numeric_semantics_are_not_guessed(self):
        for expression in ("1.5F + 2.5F", " * ".join(["1000000000.0R"] * 36)):
            self.client.response(self.client.send("evaluate", expression=expression, frameId=self.frame), success=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
