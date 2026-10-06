import json
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
import importlib.util

spec = importlib.util.spec_from_file_location("merge_tool_definitions", os.path.join(ROOT, "scripts", "merge-tool-definitions.py"))
merger = importlib.util.module_from_spec(spec)
spec.loader.exec_module(merger)

REAL = os.path.join(ROOT, "src", "GxMcp.Gateway", "tool_definitions.json")


def tool(name, description="Use it. Then stop.", **properties):
    return {"name": name, "description": description, "inputSchema": {"type": "object", "properties": properties}}


class MergeToolDefinitionsTests(unittest.TestCase):
    def run_merge(self, base, ours, theirs):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for label, tools in (("base", base), ("ours", ours), ("theirs", theirs)):
                path = os.path.join(directory, label + ".json")
                with open(path, "w", encoding="utf-8", newline="") as handle:
                    handle.write(merger.render(tools))
                paths.append(path)
            output = os.path.join(directory, "out.json")
            code = merger.main([*paths, "-o", output])
            if code:
                return code, None
            with open(output, encoding="utf-8") as handle:
                return code, json.load(handle)

    def test_render_reproduces_the_real_file_byte_for_byte(self):
        with open(REAL, encoding="utf-8", newline="") as handle:
            text = handle.read()
        self.assertEqual(text, merger.render(json.loads(text)))

    def test_different_fields_of_one_tool_merge(self):
        base = [tool("a", x={"type": "string"})]
        ours = [tool("a", x={"type": "string"}, added={"type": "boolean"})]
        theirs = [tool("a", description="Use it better. Then stop.", x={"type": "string"})]
        code, merged = self.run_merge(base, ours, theirs)
        self.assertEqual(code, 0)
        self.assertEqual(merged[0]["description"], "Use it better. Then stop.")
        self.assertIn("added", merged[0]["inputSchema"]["properties"])

    def test_property_added_on_one_side_only_is_kept(self):
        base = [tool("a", x={"type": "string"})]
        code, merged = self.run_merge(base, [tool("a", x={"type": "string"}, mine={"type": "boolean"})],
                                      [tool("a", description="Use it better. Then stop.", x={"type": "string"})])
        self.assertEqual(code, 0)
        self.assertIn("mine", merged[0]["inputSchema"]["properties"])

    def test_two_sentences_of_one_description_merge(self):
        base = [tool("a", description="One. Two. Three.")]
        code, merged = self.run_merge(base, [tool("a", description="One edited. Two. Three.")],
                                      [tool("a", description="One. Two. Three edited.")])
        self.assertEqual(code, 0)
        self.assertEqual(merged[0]["description"], "One edited. Two. Three edited.")

    def test_same_sentence_changed_differently_conflicts(self):
        base = [tool("a", description="One. Two.")]
        code, _ = self.run_merge(base, [tool("a", description="Mine. Two.")], [tool("a", description="Yours. Two.")])
        self.assertEqual(code, 1)

    def test_tools_added_on_each_side_are_kept_and_deletions_apply(self):
        base = [tool("a"), tool("gone")]
        code, merged = self.run_merge(base, [tool("a"), tool("ours")], [tool("a"), tool("gone"), tool("theirs")])
        self.assertEqual(code, 0)
        self.assertEqual([t["name"] for t in merged], ["a", "ours", "theirs"])

    def test_deleting_a_tool_the_other_side_edited_conflicts(self):
        base = [tool("a")]
        code, _ = self.run_merge(base, [], [tool("a", description="Changed.")])
        self.assertEqual(code, 1)

    def test_conflict_does_not_replace_an_existing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for label, tools in (("base", [tool("a")]), ("ours", [tool("a", description="Mine.")]), ("theirs", [tool("a", description="Yours.")])):
                path = os.path.join(directory, label + ".json")
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(merger.render(tools))
                paths.append(path)
            output = os.path.join(directory, "out.json")
            with open(output, "w", encoding="utf-8") as handle:
                handle.write("keep this file")
            self.assertEqual(merger.main([*paths, "-o", output]), 1)
            with open(output, encoding="utf-8") as handle:
                self.assertEqual(handle.read(), "keep this file")

    def test_duplicate_tool_names_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "duplicate.json")
            with open(path, "w", encoding="utf-8") as handle:
                json.dump([tool("a"), tool("a")], handle)
            with self.assertRaisesRegex(ValueError, "duplicate tool name"):
                merger.load_tools(path)


if __name__ == "__main__":
    unittest.main()
