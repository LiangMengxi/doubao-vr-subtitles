"""Meaningful pixel-format and crop regression checks, with no running VR required."""
import unittest
import numpy as np
from unittest.mock import Mock
from capture_source import to_rgba
from vr_overlay import SubtitleOverlay


class FrameConversionTests(unittest.TestCase):
    def test_channel_order_and_alpha_are_preserved(self):
        frame = np.array([[[10, 20, 200, 123]]], dtype=np.uint8)
        rgba = to_rgba(frame)
        self.assertEqual(rgba.tolist(), [[[200, 20, 10, 123]]])
        self.assertTrue(rgba.flags.c_contiguous)

    def test_trim_then_crop_toolbar(self):
        frame = np.zeros((9, 10, 4), dtype=np.uint8)
        frame[2:8, 3:9] = [1, 2, 3, 255]
        frame[5:8, 3:9, 2] = 200
        rgba = to_rgba(frame, crop_top=3)
        self.assertEqual(rgba.shape, (3, 6, 4))
        self.assertTrue(np.all(rgba[:, :, 0] == 200))

    def test_excess_crop_always_retains_one_row(self):
        rgba = to_rgba(np.full((5, 7, 4), 255, dtype=np.uint8), crop_top=999)
        self.assertEqual(rgba.shape, (1, 7, 4))

    def test_fully_transparent_frame_remains_transparent(self):
        rgba = to_rgba(np.zeros((5, 7, 4), dtype=np.uint8))
        self.assertEqual(rgba.shape, (5, 7, 4))
        self.assertEqual(int(rgba[:, :, 3].sum()), 0)

    def test_capture_buffer_is_not_modified(self):
        frame = np.arange(4 * 5 * 4, dtype=np.uint8).reshape(4, 5, 4)
        original = frame.copy()
        to_rgba(frame, crop_top=1)
        np.testing.assert_array_equal(original, frame)

    def test_overlay_event_empty_tuple_exits_immediately(self):
        overlay = object.__new__(SubtitleOverlay)
        overlay.handle = 1
        overlay.api = Mock()
        overlay.api.pollNextOverlayEvent.return_value = (False, None)
        self.assertFalse(overlay.quit_requested())
        overlay.api.pollNextOverlayEvent.assert_called_once()


if __name__ == "__main__":
    unittest.main()
