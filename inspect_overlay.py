"""Read-only check for an overlay left behind after its controller closes."""
import argparse
import json
import openvr


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--key", default="local.doubao.subtitle.bridge")
    args = parser.parse_args()
    openvr.init(openvr.VRApplication_Background)
    try:
        api = openvr.VROverlay()
        try:
            handle = api.findOverlay(args.key)
        except openvr.error_code.OverlayError:
            print(json.dumps({"exists": False}))
            return
        print(json.dumps({"exists": True, "visible": api.isOverlayVisible(handle),
                          "owner_pid": api.getOverlayRenderingPid(handle)}))
    finally:
        openvr.shutdown()


if __name__ == "__main__":
    main()
