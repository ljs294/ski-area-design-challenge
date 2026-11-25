"""The building kit (task 4.50): walls with openings, gable roofs, foundations, fixtures and lights, on top of the lift
kit (tools/assets/lifts/liftkit), whose MeshBuilder, primitives, palette, detail atlas, AO and FBX export it reuses.

The building frame is the lift frame (liftkit/frame.py) with its origin at the footprint's centre on the finished
floor: u along the long axis (Unity +Z, the game's bearing), v across it (Unity +X), w up. Specs are in millimetres.
"""
import os
import sys

LIFTS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "lifts"))
if LIFTS not in sys.path:
    sys.path.append(LIFTS)   # after the buildings own folders: render_review is theirs, liftkit only the lifts have
