"""Build-time only: synthesize a tiny deterministic model for the native verifier.

Y = X @ (2*I) = 2*X, so input [1,2,3,4] -> [2,4,6,8]. Not committed to git; the
verifier uses it solely to prove the OpenVINO EP executes a real graph.
"""
import sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

out = sys.argv[1] if len(sys.argv) > 1 else "model.onnx"
W = numpy_helper.from_array((np.eye(4, dtype=np.float32) * 2.0), name="W")
node = helper.make_node("MatMul", ["X", "W"], ["Y"])
g = helper.make_graph(
    [node], "verify",
    [helper.make_tensor_value_info("X", TensorProto.FLOAT, [1, 4])],
    [helper.make_tensor_value_info("Y", TensorProto.FLOAT, [1, 4])],
    [W],
)
m = helper.make_model(g, opset_imports=[helper.make_opsetid("", 13)])
m.ir_version = 9
onnx.save(m, out)
print(f"wrote {out}")
