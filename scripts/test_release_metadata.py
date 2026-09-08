import unittest
from release_metadata import resolve


class ReleaseMetadataTests(unittest.TestCase):
    def test_main_is_snapshot(self):
        self.assertEqual(resolve("1.2.3", "push", "refs/heads/main", "12", "99", "2"),
                         ("1.2.3-snapshot.12.2", "snapshot-99-2", True))

    def test_matching_tag_is_release(self):
        self.assertEqual(resolve("1.2.3", "push", "refs/tags/v1.2.3", "12", "99", "1"),
                         ("1.2.3", "v1.2.3", False))

    def test_mismatched_tag_rejected(self):
        with self.assertRaises(ValueError):
            resolve("1.2.3", "push", "refs/tags/v9.0.0", "12", "99", "1")

    def test_manual_run_on_tag_is_not_versioned_release(self):
        self.assertTrue(resolve("1.2.3", "workflow_dispatch", "refs/tags/v1.2.3", "12", "99", "1")[1].startswith("snapshot-"))

    def test_prerelease_tag_remains_prerelease(self):
        self.assertEqual(resolve("1.2.3-rc.1", "push", "refs/tags/v1.2.3-rc.1", "12", "99", "1"),
                         ("1.2.3-rc.1", "v1.2.3-rc.1", True))


if __name__ == "__main__":
    unittest.main()
