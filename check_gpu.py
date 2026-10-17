"""Optional real GPU/OpenVR integration check; keeps test overlay hidden."""
import json
import numpy as np
from vr_overlay import SubtitleOverlay


def main():
    overlay = SubtitleOverlay('local.doubao.subtitle.gpu-check')
    submitted = 0
    try:
        for index in range(120):
            image = np.zeros((96, 384, 4), dtype=np.uint8)
            image[:, :, 3] = 220
            image[20:60, index:index+80] = [index * 2, 180, 240, 255]
            if not overlay.submit(image, show=False):
                raise AssertionError('GPU upload timed out')
            submitted += 1
        assert overlay.pool.allocations == 3, overlay.pool.allocations
        texture = overlay.pool.textures[overlay.pool.index]
        np.testing.assert_array_equal(overlay.pool.readback(texture), image)
        # Expanding the source creates a new ring without clearing the overlay.
        expanded = np.full((480, 384, 4), [32, 48, 72, 255], dtype=np.uint8)
        assert overlay.submit(expanded, show=False)
        texture = overlay.pool.textures[overlay.pool.index]
        np.testing.assert_array_equal(overlay.pool.readback(texture), expanded)
        assert not overlay.api.isOverlayVisible(overlay.handle)
        print(json.dumps(dict(frames=submitted, textures_for_120_frames=3,
                              gpu_pixels_match=True, resized_pixels_match=True,
                              visible=False)))
    finally:
        overlay.close()


if __name__ == '__main__':
    main()
