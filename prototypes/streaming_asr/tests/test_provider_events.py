import unittest

from provider_events import (
    AsrError,
    ConnectionState,
    PartialTranscript,
    StableTranscript,
)


class ProviderEventsTest(unittest.TestCase):
    def test_standard_event_types_are_named_for_provider_boundary(self):
        partial = PartialTranscript(text="你好", raw_type="result-generated", timestamp=1.0)
        stable = StableTranscript(text="你好世界", raw_type="result-generated", timestamp=2.0)
        error = AsrError(message="bad request", raw_type="task-failed", timestamp=3.0)
        state = ConnectionState(state="connected", raw_type="task-started", timestamp=4.0)

        self.assertEqual(partial.kind, "partial")
        self.assertEqual(stable.kind, "stable")
        self.assertEqual(error.kind, "error")
        self.assertEqual(state.kind, "connection_state")


if __name__ == "__main__":
    unittest.main()
