import importlib.util
import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "validate-typed-visual-authoring.py"
CORPUS = ROOT / "plans" / "108-typed-visual-authoring.acceptance.json"


def load_module():
    spec = importlib.util.spec_from_file_location("validate_typed_visual_authoring", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ValidateTypedVisualAuthoringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_module()

    def test_design_corpus_is_valid_and_not_executed(self):
        document = json.loads(CORPUS.read_text(encoding="utf-8"))
        self.assertEqual(self.module.validate(document), [])
        self.assertEqual(document["status"], "DESIGN_NOT_EXECUTED")
        self.assertTrue(document["executionContract"]["requiresLiveSdk"])

    def test_duplicate_or_missing_scenario_is_rejected(self):
        document = json.loads(CORPUS.read_text(encoding="utf-8"))
        document["scenarios"][-1]["id"] = document["scenarios"][0]["id"]
        errors = self.module.validate(document)
        self.assertTrue(any("duplicate scenario id" in error for error in errors))
        self.assertTrue(any("scenario ids must be VA01..VA10" in error for error in errors))

    def test_raw_xml_route_and_implicit_rollback_are_rejected(self):
        document = json.loads(CORPUS.read_text(encoding="utf-8"))
        document["typedContracts"]["receipt"]["mutationRoutes"].append("raw-xml")
        document["typedContracts"]["request"]["required"].remove("rollbackOnFailure")
        errors = self.module.validate(document)
        self.assertIn("receipt mutationRoutes must not include raw-xml", errors)
        self.assertIn("request required fields must include rollbackOnFailure", errors)


if __name__ == "__main__":
    unittest.main()
