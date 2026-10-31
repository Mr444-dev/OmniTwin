using OmniTwin.Contracts;
using UnityEngine;
using UnityEngine.Rendering;

namespace OmniTwin.Simulation.World
{
    /// <summary>Renders one bounded holographic footprint, bracket frame, and camera-facing label.</summary>
    [DisallowMultipleComponent]
    public sealed class WorldSpaceTacticalCallout : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private readonly Vector3[] _ringPoints = new Vector3[49];
        private readonly Vector3[] _bracketVertices = new Vector3[24];
        private readonly int[] _bracketIndices = new int[24];
        private MaterialPropertyBlock _block;
        private LineRenderer _ring;
        private Mesh _bracketMesh;
        private MeshRenderer _bracketRenderer;
        private TextMesh _label;
        private Camera _camera;
        private Transform _bracketRoot;
        private Transform _ringRoot;
        private float _nextFacing;
        private float _labelHeightOffset;
        private float _labelLateralOffset;

        /// <summary>Builds child visuals at runtime from shared URP materials.</summary>
        public void Configure(Material sharedUnlit, string label, Color color, float radius, float height, int obstacleId)
        {
            if (sharedUnlit == null || string.IsNullOrWhiteSpace(label) || !Finite(radius) || radius <= 0 || !Finite(height) || height <= 0)
                throw new System.ArgumentException("A bounded label, dimensions, and shared material are required.");
            if (_block == null) _block = new MaterialPropertyBlock();
            _labelHeightOffset = 0.85f + (obstacleId % 2 == 0 ? 0.45f : 0f);
            _labelLateralOffset = obstacleId % 2 == 0 ? 0.55f : -0.55f;
            if (_label != null) { UpdateDetails(label, color, radius, height); return; }
            _camera = Camera.main;
            var labelObject = new GameObject("World-space callout"); labelObject.transform.SetParent(transform, false);
            labelObject.transform.localPosition = new Vector3(_labelLateralOffset, height + _labelHeightOffset, 0);
            _label = labelObject.AddComponent<TextMesh>(); _label.text = label; _label.fontSize = 96; _label.characterSize = 0.07f;
            _label.anchor = TextAnchor.LowerCenter; _label.alignment = TextAlignment.Center; _label.color = color;
            _label.richText = false;

            var ringObject = new GameObject("Safety perimeter ring"); ringObject.transform.SetParent(transform, false); _ringRoot = ringObject.transform;
            _ring = ringObject.AddComponent<LineRenderer>(); _ring.useWorldSpace = false; _ring.loop = false; _ring.positionCount = 49;
            _ring.sharedMaterial = sharedUnlit; _ring.startWidth = 0.025f; _ring.endWidth = 0.025f;
            _ring.shadowCastingMode = ShadowCastingMode.Off; _ring.receiveShadows = false;

            var bracketObject = new GameObject("Tactical frame brackets"); bracketObject.transform.SetParent(transform, false); _bracketRoot = bracketObject.transform;
            var filter = bracketObject.AddComponent<MeshFilter>(); _bracketRenderer = bracketObject.AddComponent<MeshRenderer>();
            _bracketRenderer.sharedMaterial = sharedUnlit; _bracketRenderer.shadowCastingMode = ShadowCastingMode.Off; _bracketRenderer.receiveShadows = false;
            _bracketMesh = new Mesh { name = "Tactical corner brackets" }; _bracketMesh.MarkDynamic(); filter.sharedMesh = _bracketMesh;
            for (var i = 0; i < _bracketIndices.Length; i++) _bracketIndices[i] = i;
            UpdateDetails(label, color, radius, height);
        }

        /// <summary>Updates stable descriptive content only when the incoming track has changed.</summary>
        public void UpdateDetails(string label, Color color, float radius, float height)
        {
            if (_label == null) return;
            if (_label.text != label) _label.text = label;
            _label.color = color;
            var diameter = 2 * radius;
            _ringRoot.localScale = new Vector3(diameter + 0.8f, 1, diameter + 0.8f);
            for (var i = 0; i < 48; i++)
            {
                var angle = (float)(i * (System.Math.PI * 2 / 48));
                _ringPoints[i] = new Vector3(Mathf.Cos(angle) * 0.5f, 0.035f, Mathf.Sin(angle) * 0.5f);
            }
            _ringPoints[48] = _ringPoints[0];
            _ring.SetPositions(_ringPoints);
            _ring.startColor = color; _ring.endColor = color;
            _block.SetColor(BaseColor, color); _ring.SetPropertyBlock(_block); _bracketRenderer.SetPropertyBlock(_block);
            _bracketRoot.localScale = new Vector3(diameter, height, diameter);
            BuildBracketMesh();
            _label.transform.localPosition = new Vector3(_labelLateralOffset, height + _labelHeightOffset, 0);
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextFacing) return;
            _nextFacing = Time.unscaledTime + 0.05f;
            if (_camera == null) _camera = Camera.main;
            if (_camera != null) _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - _camera.transform.position, _camera.transform.up);
        }

        private void BuildBracketMesh()
        {
            var cursor = 0;
            for (var xSign = -1; xSign <= 1; xSign += 2)
                for (var zSign = -1; zSign <= 1; zSign += 2)
                {
                    var corner = new Vector3(xSign * 0.5f, 0, zSign * 0.5f);
                    var xTick = corner + new Vector3(-xSign * 0.22f, 0, 0);
                    var zTick = corner + new Vector3(0, 0, -zSign * 0.22f);
                    var top = corner + Vector3.up;
                    PutLine(corner, xTick, ref cursor); PutLine(corner, zTick, ref cursor); PutLine(corner, top, ref cursor);
                }
            _bracketMesh.Clear(); _bracketMesh.vertices = _bracketVertices; _bracketMesh.SetIndices(_bracketIndices, MeshTopology.Lines, 0); _bracketMesh.RecalculateBounds();
        }

        private void PutLine(Vector3 from, Vector3 to, ref int cursor)
        { _bracketVertices[cursor] = from; _bracketIndices[cursor] = cursor++; _bracketVertices[cursor] = to; _bracketIndices[cursor] = cursor++; }

        private void OnDestroy() { if (_bracketMesh != null) Destroy(_bracketMesh); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
