using System;
using System.IO;
using OmniTwin.Simulation.CameraControl;
using OmniTwin.Simulation.Network;
using OmniTwin.Simulation.UI;
using OmniTwin.Simulation.World;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Runtime;
using OmniTwin.Simulation.Media;
using OmniTwin.Simulation.Sensors;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using UnityEngine.AI;

namespace OmniTwin.Simulation.Editor
{
    /// <summary>Creates the calibrated-world presentation scene using Editor APIs without replacing existing scenes.</summary>
    public static class TacticalSceneBuilder
    {
        /// <summary>The scene authored by this tool.</summary>
        public const string ScenePath = "Assets/_OmniTwin/Scenes/TacticalDesk.unity";
        private const string RootPath = "Assets/_OmniTwin";

        /// <summary>Creates a desktop URP workspace and wires the actual stream, visualizer, and HUD.</summary>
        [MenuItem("OmniTwin/Create Tactical Desk Scene")]
        public static void CreateScene()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play mode before authoring the scene.");
            if (File.Exists(ScenePath)) throw new InvalidOperationException("TacticalDesk already exists; open it instead of replacing it.");
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) throw new InvalidOperationException("An active Universal Render Pipeline asset is required.");
            if (!EditorSceneManager.SaveOpenScenes()) throw new InvalidOperationException("Open scenes could not be saved.");
            Directory.CreateDirectory(RootPath + "/Scenes");
            Directory.CreateDirectory(RootPath + "/Materials");
            Directory.CreateDirectory(RootPath + "/Settings");
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "TacticalDesk";

            var floorMaterial = new Material(pipeline.defaultMaterial) { name = "Tactical Floor" };
            floorMaterial.SetColor("_BaseColor", new Color(0.035f, 0.06f, 0.085f));
            floorMaterial.SetFloat("_Smoothness", 0.45f);
            var floorPath = RootPath + "/Materials/TacticalFloor.mat";
            SaveNew(floorMaterial, floorPath);
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null) throw new InvalidOperationException("The installed URP Unlit shader is missing.");
            var gridMaterial = new Material(unlit) { name = "Tactical Grid" };
            gridMaterial.SetColor("_BaseColor", new Color(0.045f, 0.33f, 0.39f));
            SaveNew(gridMaterial, RootPath + "/Materials/TacticalGrid.mat");
            var borderMaterial = new Material(unlit) { name = "Workspace Boundary" };
            borderMaterial.SetColor("_BaseColor", new Color(0.08f, 0.9f, 0.78f));
            SaveNew(borderMaterial, RootPath + "/Materials/WorkspaceBoundary.mat");

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Reference Workspace 20 x 12.5 m";
            floor.transform.position = new Vector3(0, -0.1f, 0);
            floor.transform.localScale = new Vector3(20, 0.2f, 12.5f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
            Undo.RegisterCreatedObjectUndo(floor, "Create OmniTwin floor");
            var surface = floor.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            if (surface.navMeshData == null) throw new InvalidOperationException("Reference floor NavMesh build failed.");
            var navData = surface.navMeshData;
            surface.RemoveData();
            SaveNew(navData, RootPath + "/Settings/TacticalFloorNavMesh.asset");
            surface.navMeshData = navData;
            surface.AddData();

            var grid = Root("Reference Grid");
            for (var x = -10; x <= 10; x++) Line(grid.transform, "X " + x, new Vector3(x, 0.01f, -6.25f), new Vector3(x, 0.01f, 6.25f), gridMaterial, x == 0 ? 0.025f : 0.012f);
            for (var z = -6; z <= 6; z++) Line(grid.transform, "Z " + z, new Vector3(-10, 0.01f, z), new Vector3(10, 0.01f, z), gridMaterial, z == 0 ? 0.025f : 0.012f);
            Line(grid.transform, "Boundary south", new Vector3(-10, 0.02f, -6.25f), new Vector3(10, 0.02f, -6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary north", new Vector3(-10, 0.02f, 6.25f), new Vector3(10, 0.02f, 6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary west", new Vector3(-10, 0.02f, -6.25f), new Vector3(-10, 0.02f, 6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary east", new Vector3(10, 0.02f, -6.25f), new Vector3(10, 0.02f, 6.25f), borderMaterial, 0.035f);

            var connection = Root("TwinConnection").AddComponent<TwinWebSocketClient>();
            var spawner = Root("TrackedObstacles").AddComponent<DynamicObstacleSpawner>();
            Assign(spawner, "stream", connection);

            var cameraObject = Root("Tactical Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<UnityEngine.Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.012f, 0.022f, 0.032f);
            camera.fieldOfView = 43;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 150;
            camera.allowHDR = true;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<TacticalCameraController>().ResetView();
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = true;
            cameraData.volumeLayerMask = 1;
            cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;

            var key = Root("Workspace Key Light").AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(0.65f, 0.86f, 1);
            key.intensity = 2;
            key.shadows = LightShadows.Soft;
            key.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.12f, 0.19f, 0.24f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Tactical Tone";
            var tonemap = profile.Add<Tonemapping>(true);
            tonemap.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.2f);
            bloom.threshold.Override(1);
            var profilePath = RootPath + "/Settings/TacticalTone.asset";
            SaveNew(profile, profilePath);
            foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            var volume = Root("Tactical Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;

            CompleteScene();
        }

        /// <summary>Completes HUD and scene serialization after geometry authoring, including recovery from an interrupted setup.</summary>
        [MenuItem("OmniTwin/Complete Tactical Desk Setup")]
        public static void CompleteScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.name != "TacticalDesk" ||
                (!string.IsNullOrEmpty(scene.path) && scene.path != ScenePath))
                throw new InvalidOperationException("Open the authored TacticalDesk scene in Edit mode first.");
            var connections = UnityEngine.Object.FindObjectsByType<TwinWebSocketClient>();
            if (connections.Length != 1 || GameObject.Find("Tactical HUD") != null)
                throw new InvalidOperationException("Completion requires one stream connection and no existing Tactical HUD.");
            var connection = connections[0];
            var sourceFont = AssetDatabase.LoadAssetAtPath<Font>(RootPath + "/UI/TacticalHud/Fonts/Rajdhani-Medium.ttf");
            if (sourceFont == null) throw new InvalidOperationException("The licensed Rajdhani font must be imported first.");
            var font = FontAsset.CreateFontAsset(sourceFont);
            if (font == null) throw new InvalidOperationException("The font face could not be loaded; enable Include Font Data on its importer.");
            font.name = "OmniTwin Sans";
            SaveNew(font, RootPath + "/UI/TacticalHud/OmniTwinSans.asset");
            if (font.material != null) AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (var texture in font.atlasTextures) if (texture != null) AssetDatabase.AddObjectToAsset(texture, font);
            AssetDatabase.ImportAsset(RootPath + "/UI/TacticalHud/TacticalHud.uss", ImportAssetOptions.ForceUpdate);
            var text = ScriptableObject.CreateInstance<PanelTextSettings>();
            text.fallbackFontAssets = new System.Collections.Generic.List<FontAsset> { font };
            SaveNew(text, RootPath + "/UI/TacticalHud/TacticalTextSettings.asset");
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.textSettings = text;
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(RootPath + "/UI/TacticalHud/TacticalHud.tss");
            if (panel.themeStyleSheet == null) throw new InvalidOperationException("Tactical HUD theme is missing.");
            SaveNew(panel, RootPath + "/UI/TacticalHud/TacticalPanelSettings.asset");
            var document = Root("Tactical HUD").AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RootPath + "/UI/TacticalHud/TacticalHud.uxml");
            if (document.visualTreeAsset == null) throw new InvalidOperationException("Tactical HUD layout is missing.");
            Assign(document.gameObject.AddComponent<TacticalHudController>(), "stream", connection);
            AddSwarmToScene();
            AddMediaToScene();
            AddVisualSystemsToScene();

            PlayerSettings.companyName = "OmniTwin";
            PlayerSettings.productName = "OmniTwin";
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.runInBackground = true;
            EditorSettings.serializationMode = SerializationMode.ForceText;
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Tactical scene could not be saved.");
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = connection.gameObject;
            Debug.Log("OmniTwin TacticalDesk created with authored simulation agents; movement requires current perception.");
        }

        /// <summary>Wires the opt-in camera preview and operator-initiated voice controller in the existing scene.</summary>
        [MenuItem("OmniTwin/Add Operator Media To Tactical Desk")]
        public static void AddMediaToScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.name != "TacticalDesk" || (!string.IsNullOrEmpty(scene.path) && scene.path != ScenePath))
                throw new InvalidOperationException("Open TacticalDesk in Edit mode.");
            var hud = UnityEngine.Object.FindObjectsByType<TacticalHudController>(); var controllers = UnityEngine.Object.FindObjectsByType<MediaConsoleController>();
            if (hud.Length != 1 || controllers.Length > 1) throw new InvalidOperationException("One HUD and at most one media controller are required.");
            var controller = controllers.Length == 0 ? Root("Operator Media").AddComponent<MediaConsoleController>() : controllers[0];
            Assign(hud[0], "media", controller);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!string.IsNullOrEmpty(scene.path) && !EditorSceneManager.SaveScene(scene)) throw new IOException("Media scene wiring could not be saved.");
        }

        /// <summary>Adds physical sensors and optional visual services without replacing existing authored assets.</summary>
        [MenuItem("OmniTwin/Add Mask Lidar And Scenery Systems")]
        public static void AddVisualSystemsToScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.name != "TacticalDesk" || scene.path != ScenePath) throw new InvalidOperationException("Open the saved TacticalDesk in Edit mode.");
            var hud = UnityEngine.Object.FindObjectsByType<TacticalHudController>(); var services = UnityEngine.Object.FindObjectsByType<VisualServicesController>();
            if (hud.Length != 1 || services.Length > 1) throw new InvalidOperationException("One tactical HUD and at most one visual service are required.");
            var floor = GameObject.Find("Reference Workspace 20 x 12.5 m");
            if (floor == null || floor.GetComponent<Renderer>() == null) throw new InvalidOperationException("Reference workspace renderer is missing.");
            var service = services.Length == 0 ? Root("Visual Services").AddComponent<VisualServicesController>() : services[0];
            Assign(service, "workspaceSurface", floor.GetComponent<Renderer>()); Assign(hud[0], "visuals", service);
            foreach (var agent in UnityEngine.Object.FindObjectsByType<SwarmAgentController>()) if (agent.GetComponent<LidarSensor>() == null) Undo.AddComponent<LidarSensor>(agent.gameObject);
            EditorSceneManager.MarkSceneDirty(scene); if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Visual systems scene save failed.");
        }

        /// <summary>Adds three real NavMesh vehicles and two aerial simulation entities to the existing authored scene.</summary>
        [MenuItem("OmniTwin/Add Swarm To Tactical Desk")]
        public static void AddSwarmToScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.name != "TacticalDesk" ||
                (!string.IsNullOrEmpty(scene.path) && scene.path != ScenePath)) throw new InvalidOperationException("Open TacticalDesk in Edit mode.");
            var connection = UnityEngine.Object.FindObjectsByType<TwinWebSocketClient>();
            var hud = UnityEngine.Object.FindObjectsByType<TacticalHudController>();
            if (connection.Length != 1 || hud.Length != 1) throw new InvalidOperationException("One stream and HUD are required.");
            var existing = UnityEngine.Object.FindObjectsByType<PolicyExecutor>();
            if (existing.Length > 0)
            {
                if (existing.Length != 1 || existing[0].Agents.Count != 5) throw new InvalidOperationException("Existing swarm configuration requires review.");
                Assign(hud[0], "executor", existing[0]);
                EditorSceneManager.SaveScene(scene, ScenePath); return;
            }
            if (UnityEngine.AI.NavMesh.CalculateTriangulation().vertices.Length == 0) throw new InvalidOperationException("The reference NavMesh must be loaded first.");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null) throw new InvalidOperationException("URP Unlit is required.");
            Material MaterialFor(string name, Color color)
            {
                var path = RootPath + "/Materials/" + name + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null) return material;
                material = new Material(unlit) { name = name }; material.SetColor("_BaseColor", color); SaveNew(material, path); return material;
            }
            var groundMaterial = MaterialFor("SwarmGround", new Color(0.08f, 0.83f, 0.93f));
            var aerialMaterial = MaterialFor("SwarmAerial", new Color(1, 0.65f, 0.18f));
            var root = Root("Swarm Agents");
            var controllers = new SwarmAgentController[5];
            for (var index = 0; index < controllers.Length; index++)
            {
                var aerial = index >= 3; var id = index + 1;
                var entity = new GameObject((aerial ? "DRONE_AERIAL_" : "AGV_GROUND_") + id);
                entity.transform.SetParent(root.transform);
                var altitude = index == 4 ? 2.8f : 2.2f;
                entity.transform.position = aerial ? new Vector3(index == 3 ? -6 : -3, altitude, 1) : new Vector3(-7 + index * 2, 0, -3);
                var body = GameObject.CreatePrimitive(aerial ? PrimitiveType.Sphere : PrimitiveType.Cube);
                body.name = "Simulation body"; body.transform.SetParent(entity.transform, false);
                body.transform.localPosition = aerial ? Vector3.zero : new Vector3(0, 0.3f, 0);
                body.transform.localScale = aerial ? new Vector3(0.55f, 0.22f, 0.55f) : new Vector3(0.6f, 0.45f, 0.8f);
                body.GetComponent<Renderer>().sharedMaterial = aerial ? aerialMaterial : groundMaterial;
                if (!aerial)
                {
                    var navigation = entity.AddComponent<NavMeshAgent>();
                    navigation.radius = 0.35f; navigation.height = 0.7f; navigation.speed = 1.5f; navigation.acceleration = 8;
                    navigation.angularSpeed = 180; navigation.stoppingDistance = 0.12f; navigation.autoBraking = true;
                    navigation.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                    navigation.avoidancePriority = 20 + index * 10;
                }
                var pathObject = new GameObject("Planned trajectory"); pathObject.transform.SetParent(entity.transform, false);
                var pathLine = pathObject.AddComponent<LineRenderer>(); pathLine.sharedMaterial = aerial ? aerialMaterial : groundMaterial;
                pathLine.useWorldSpace = true; pathLine.startWidth = 0.045f; pathLine.endWidth = 0.025f; pathLine.positionCount = 0;
                pathLine.shadowCastingMode = ShadowCastingMode.Off; pathLine.receiveShadows = false;
                controllers[index] = entity.AddComponent<SwarmAgentController>();
                controllers[index].Configure(id, aerial ? AgentRole.AerialDrone : AgentRole.GroundVehicle, altitude, pathLine);
                entity.AddComponent<LidarSensor>();
            }
            var executor = Root("Swarm Control").AddComponent<PolicyExecutor>(); executor.Configure(connection[0], controllers);
            Assign(hud[0], "executor", executor);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Swarm scene save failed.");
            AssetDatabase.SaveAssets();
            Debug.Log("OmniTwin: three NavMesh vehicles and two aerial agents authored. Source loss enforces EmergencyHold.");
        }

        private static void SaveNew(UnityEngine.Object asset, string path)
        {
            if (File.Exists(path)) throw new IOException("Asset already exists: " + path);
            AssetDatabase.CreateAsset(asset, path);
        }
        private static GameObject Root(string name)
        {
            var root = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(root, "Create " + name);
            return root;
        }
        private static void Assign(UnityEngine.Object owner, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(owner);
            var property = serialized.FindProperty(field) ?? throw new InvalidOperationException("Missing serialized reference: " + field);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void Line(Transform parent, string name, Vector3 start, Vector3 end, Material material, float width)
        {
            var child = Root(name);
            child.transform.SetParent(parent, false);
            var line = child.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.positionCount = 2;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            line.startWidth = width;
            line.endWidth = width;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
        }
    }
}
