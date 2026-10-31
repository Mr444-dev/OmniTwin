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
            floorMaterial.SetColor("_BaseColor", new Color(0.065f, 0.095f, 0.12f));
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
            var minorGridMaterial = new Material(unlit) { name = "Tactical Minor Grid" };
            minorGridMaterial.SetColor("_BaseColor", new Color(0.025f, 0.12f, 0.15f));
            SaveNew(minorGridMaterial, RootPath + "/Materials/TacticalMinorGrid.mat");

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
            for (var halfX = -19; halfX <= 19; halfX += 2)
            {
                var x = halfX * 0.5f;
                Line(grid.transform, "Minor X " + x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), new Vector3(x, 0.008f, -6.25f), new Vector3(x, 0.008f, 6.25f), minorGridMaterial, 0.006f);
            }
            for (var halfZ = -11; halfZ <= 11; halfZ += 2)
            {
                var z = halfZ * 0.5f;
                Line(grid.transform, "Minor Z " + z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), new Vector3(-10, 0.008f, z), new Vector3(10, 0.008f, z), minorGridMaterial, 0.006f);
            }
            for (var x = -10; x <= 10; x++) Line(grid.transform, "X " + x, new Vector3(x, 0.01f, -6.25f), new Vector3(x, 0.01f, 6.25f), gridMaterial, x == 0 ? 0.025f : 0.012f);
            for (var z = -6; z <= 6; z++) Line(grid.transform, "Z " + z, new Vector3(-10, 0.01f, z), new Vector3(10, 0.01f, z), gridMaterial, z == 0 ? 0.025f : 0.012f);
            Line(grid.transform, "Boundary south", new Vector3(-10, 0.02f, -6.25f), new Vector3(10, 0.02f, -6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary north", new Vector3(-10, 0.02f, 6.25f), new Vector3(10, 0.02f, 6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary west", new Vector3(-10, 0.02f, -6.25f), new Vector3(-10, 0.02f, 6.25f), borderMaterial, 0.035f);
            Line(grid.transform, "Boundary east", new Vector3(10, 0.02f, -6.25f), new Vector3(10, 0.02f, 6.25f), borderMaterial, 0.035f);

            var connection = Root("TwinConnection").AddComponent<TwinWebSocketClient>();
            var spawner = Root("TrackedObstacles").AddComponent<DynamicObstacleSpawner>();
            Assign(spawner, "stream", connection);
            Assign(spawner, "hologramMaterial", CreateSurfaceMaterial("ObstacleHologram", new Color(0.07f, 0.63f, 0.78f, 0.32f), true));
            Assign(spawner, "accentMaterial", CreateSurfaceMaterial("TacticalAccent", new Color(0.2f, 0.92f, 0.95f, 1), false));

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
            RenderSettings.ambientLight = new Color(0.17f, 0.22f, 0.27f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Tactical Tone";
            var tonemap = profile.Add<Tonemapping>(true);
            tonemap.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.2f);
            bloom.threshold.Override(1);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.16f); vignette.smoothness.Override(0.48f); vignette.rounded.Override(true);
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
            var spawners = UnityEngine.Object.FindObjectsByType<DynamicObstacleSpawner>();
            if (spawners.Length != 1) throw new InvalidOperationException("One tracked obstacle spawner is required.");
            var floor = GameObject.Find("Reference Workspace 20 x 12.5 m");
            if (floor == null || floor.GetComponent<Renderer>() == null) throw new InvalidOperationException("Reference workspace renderer is missing.");
            var service = services.Length == 0 ? Root("Visual Services").AddComponent<VisualServicesController>() : services[0];
            Assign(service, "workspaceSurface", floor.GetComponent<Renderer>()); Assign(hud[0], "visuals", service);
            Assign(spawners[0], "hologramMaterial", CreateSurfaceMaterial("ObstacleHologram", new Color(0.07f, 0.63f, 0.78f, 0.32f), true));
            Assign(spawners[0], "accentMaterial", CreateLitMaterial("TacticalAccent", new Color(0.12f, 0.7f, 0.78f, 1), 0.2f, 0.78f, new Color(0.04f, 0.55f, 0.68f, 1)));
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
            var groundMaterial = CreateLitMaterial("SwarmGround", new Color(0.5f, 0.67f, 0.72f, 1), 0.42f, 0.78f, new Color(0.07f, 0.2f, 0.24f, 1));
            var aerialMaterial = CreateLitMaterial("SwarmAerial", new Color(0.62f, 0.76f, 0.72f, 1), 0.36f, 0.76f, new Color(0.1f, 0.25f, 0.18f, 1));
            var accentMaterial = CreateLitMaterial("TacticalAccent", new Color(0.12f, 0.7f, 0.78f, 1), 0.2f, 0.78f, new Color(0.04f, 0.55f, 0.68f, 1));
            var hazardMaterial = CreateLitMaterial("TacticalWarning", new Color(1, 0.4f, 0.12f, 1), 0.25f, 0.68f, new Color(0.75f, 0.18f, 0.03f, 1));
            var scanMaterial = CreateSurfaceMaterial("DroneScanCone", new Color(0.18f, 0.75f, 0.82f, 0.14f), true);
            var scanMesh = ScannerConeMesh();
            if (existing.Length > 0)
            {
                if (existing.Length != 1 || existing[0].Agents.Count != 5) throw new InvalidOperationException("Existing swarm configuration requires review.");
                Assign(hud[0], "executor", existing[0]);
                foreach (var agent in existing[0].Agents)
                {
                    UpgradeAgentRig(agent, groundMaterial, aerialMaterial, accentMaterial, hazardMaterial, scanMesh, scanMaterial);
                    var path = agent.GetComponentInChildren<LineRenderer>();
                    if (path != null) ConfigurePathLine(path, accentMaterial, agent.Role == AgentRole.AerialDrone);
                }
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Swarm presentation update failed to save.");
                AssetDatabase.SaveAssets(); return;
            }
            if (UnityEngine.AI.NavMesh.CalculateTriangulation().vertices.Length == 0) throw new InvalidOperationException("The reference NavMesh must be loaded first.");
            var root = Root("Swarm Agents");
            var controllers = new SwarmAgentController[5];
            for (var index = 0; index < controllers.Length; index++)
            {
                var aerial = index >= 3; var id = index + 1;
                var entity = new GameObject((aerial ? "DRONE_AERIAL_" : "AGV_GROUND_") + id);
                entity.transform.SetParent(root.transform);
                var altitude = index == 4 ? 2.8f : 2.2f;
                entity.transform.position = aerial ? new Vector3(index == 3 ? -6 : -3, altitude, 1) : new Vector3(-7 + index * 2, 0, -3);
                if (!aerial)
                {
                    var navigation = entity.AddComponent<NavMeshAgent>();
                    navigation.radius = 0.35f; navigation.height = 0.7f; navigation.speed = 1.5f; navigation.acceleration = 8;
                    navigation.angularSpeed = 180; navigation.stoppingDistance = 0.12f; navigation.autoBraking = true;
                    navigation.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                    navigation.avoidancePriority = 20 + index * 10;
                }
                var pathObject = new GameObject("Planned trajectory"); pathObject.transform.SetParent(entity.transform, false);
                var pathLine = pathObject.AddComponent<LineRenderer>(); ConfigurePathLine(pathLine, accentMaterial, aerial);
                pathLine.positionCount = 0;
                controllers[index] = entity.AddComponent<SwarmAgentController>();
                controllers[index].Configure(id, aerial ? AgentRole.AerialDrone : AgentRole.GroundVehicle, altitude, pathLine);
                entity.AddComponent<LidarSensor>();
                UpgradeAgentRig(controllers[index], groundMaterial, aerialMaterial, accentMaterial, hazardMaterial, scanMesh, scanMaterial);
            }
            var executor = Root("Swarm Control").AddComponent<PolicyExecutor>(); executor.Configure(connection[0], controllers);
            Assign(hud[0], "executor", executor);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Swarm scene save failed.");
            AssetDatabase.SaveAssets();
            Debug.Log("OmniTwin: three NavMesh vehicles and two aerial agents authored. Source loss enforces EmergencyHold.");
        }

        /// <summary>Applies the current procedural rig, callout, URP and overlay setup to a previously authored TacticalDesk.</summary>
        [MenuItem("OmniTwin/Upgrade Tactical Desk Presentation")]
        public static void UpgradeTacticalDeskPresentation()
        {
            AddSwarmToScene();
            AddVisualSystemsToScene();
            var volume = UnityEngine.Object.FindAnyObjectByType<Volume>();
            if (volume == null || volume.sharedProfile == null) throw new InvalidOperationException("A global URP Volume profile is required.");
            if (!volume.sharedProfile.TryGet(out Vignette vignette)) vignette = volume.sharedProfile.Add<Vignette>(true);
            vignette.intensity.Override(0.16f); vignette.smoothness.Override(0.48f); vignette.rounded.Override(true);
            var floorMaterial = AssetDatabase.LoadAssetAtPath<Material>(RootPath + "/Materials/TacticalFloor.mat");
            if (floorMaterial != null && floorMaterial.HasProperty("_BaseColor"))
            {
                floorMaterial.SetColor("_BaseColor", new Color(0.065f, 0.095f, 0.12f));
                EditorUtility.SetDirty(floorMaterial);
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.17f, 0.22f, 0.27f);
            var keyLightObject = GameObject.Find("Workspace Key Light");
            if (keyLightObject != null && keyLightObject.TryGetComponent<Light>(out var keyLight))
            { keyLight.color = new Color(0.72f, 0.87f, 1f); keyLight.intensity = 2.2f; keyLight.shadows = LightShadows.Soft; }
            var fillLightObject = GameObject.Find("Tactical Fill Light");
            if (fillLightObject == null)
            {
                fillLightObject = new GameObject("Tactical Fill Light");
                Undo.RegisterCreatedObjectUndo(fillLightObject, "Create OmniTwin fill light");
            }
            var fillLight = fillLightObject.GetComponent<Light>();
            if (fillLight == null) fillLight = Undo.AddComponent<Light>(fillLightObject);
            fillLight.type = LightType.Directional; fillLight.color = new Color(0.28f, 0.78f, 0.88f);
            fillLight.intensity = 0.55f; fillLight.shadows = LightShadows.None;
            fillLight.transform.rotation = Quaternion.Euler(26f, 145f, 0f);
            var tacticalCamera = UnityEngine.Object.FindAnyObjectByType<TacticalCameraController>();
            if (tacticalCamera == null) throw new InvalidOperationException("The authored tactical camera is missing.");
            var cameraSettings = new SerializedObject(tacticalCamera);
            var overviewDistance = cameraSettings.FindProperty("initialDistance");
            if (overviewDistance == null) throw new InvalidOperationException("The tactical overview distance is not serialized.");
            overviewDistance.floatValue = 52f;
            cameraSettings.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            if (!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath)) throw new IOException("Upgraded scene could not be saved.");
            AssetDatabase.SaveAssets();
        }

        private static void UpgradeAgentRig(SwarmAgentController agent, Material ground, Material aerial, Material accent,
            Material warning, Mesh scannerMesh, Material scannerMaterial)
        {
            if (agent == null) throw new InvalidOperationException("Swarm agent reference is missing.");
            var oldBody = agent.transform.Find("Simulation body");
            if (oldBody != null)
            {
                var oldRenderer = oldBody.GetComponent<Renderer>();
                if (oldRenderer != null) oldRenderer.enabled = false;
                var oldCollider = oldBody.GetComponent<Collider>();
                if (oldCollider != null) UnityEngine.Object.DestroyImmediate(oldCollider);
            }
            var root = agent.transform.Find("OmniTwin Visual Rig");
            if (root == null) { var rig = new GameObject("OmniTwin Visual Rig"); rig.transform.SetParent(agent.transform, false); root = rig.transform; }
            var role = agent.Role;
            var lights = new System.Collections.Generic.List<Renderer>(4);
            Transform pivot;
            Transform turret = null;
            var rotors = new System.Collections.Generic.List<Transform>(4);
            if (role == AgentRole.GroundVehicle)
            {
                pivot = EnsureChild(root, "Armored chassis");
                EnsurePrimitive(pivot, "Lower armor", PrimitiveType.Cube, new Vector3(0, 0.31f, 0), new Vector3(0.78f, 0.26f, 0.98f), ground);
                EnsurePrimitive(pivot, "Upper armor", PrimitiveType.Cube, new Vector3(0, 0.48f, -0.04f), new Vector3(0.58f, 0.12f, 0.62f), aerial);
                EnsurePrimitive(pivot, "Front bumper", PrimitiveType.Cube, new Vector3(0, 0.32f, 0.51f), new Vector3(0.57f, 0.12f, 0.12f), ground);
                for (var side = -1; side <= 1; side += 2)
                    for (var axle = -1; axle <= 1; axle += 2)
                        EnsurePrimitive(pivot, "Run-flat wheel " + side + " " + axle, PrimitiveType.Cylinder,
                            new Vector3(side * 0.38f, 0.17f, axle * 0.31f), new Vector3(0.16f, 0.09f, 0.17f), ground, new Vector3(0, 0, 90));
                turret = EnsureChild(pivot, "LiDAR turret"); turret.localPosition = new Vector3(0, 0.57f, -0.04f);
                EnsurePrimitive(turret, "Turret base", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.31f, 0.04f, 0.31f), aerial);
                EnsurePrimitive(turret, "Spinning rangefinder", PrimitiveType.Sphere, new Vector3(0, 0.1f, 0), new Vector3(0.16f, 0.1f, 0.16f), accent);
                lights.Add(EnsurePrimitive(pivot, "Front status LED", PrimitiveType.Cube, new Vector3(0, 0.4f, 0.58f), new Vector3(0.42f, 0.035f, 0.025f), accent).GetComponent<Renderer>());
                lights.Add(EnsurePrimitive(pivot, "Left status LED", PrimitiveType.Sphere, new Vector3(-0.4f, 0.34f, 0), new Vector3(0.035f, 0.06f, 0.16f), accent).GetComponent<Renderer>());
                lights.Add(EnsurePrimitive(pivot, "Right status LED", PrimitiveType.Sphere, new Vector3(0.4f, 0.34f, 0), new Vector3(0.035f, 0.06f, 0.16f), accent).GetComponent<Renderer>());
            }
            else
            {
                pivot = EnsureChild(root, "Quadcopter frame");
                EnsurePrimitive(pivot, "Central avionics pod", PrimitiveType.Cube, Vector3.zero, new Vector3(0.34f, 0.14f, 0.3f), aerial);
                EnsurePrimitive(pivot, "Forward sensor nose", PrimitiveType.Sphere, new Vector3(0, -0.01f, 0.17f), new Vector3(0.12f, 0.09f, 0.1f), accent);
                EnsurePrimitive(pivot, "Crosswise carbon arms", PrimitiveType.Cube, Vector3.zero, new Vector3(0.98f, 0.055f, 0.07f), ground);
                EnsurePrimitive(pivot, "Fore-aft carbon arms", PrimitiveType.Cube, Vector3.zero, new Vector3(0.07f, 0.055f, 0.98f), ground);
                for (var x = -1; x <= 1; x += 2)
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var rotor = EnsureChild(pivot, "Ducted rotor " + x + " " + z); rotor.localPosition = new Vector3(x * 0.39f, 0.01f, z * 0.39f);
                        EnsurePrimitive(rotor, "Duct guard", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.25f, 0.035f, 0.25f), ground);
                        EnsurePrimitive(rotor, "Propeller blade A", PrimitiveType.Cube, new Vector3(0, 0.035f, 0), new Vector3(0.32f, 0.025f, 0.055f), accent);
                        EnsurePrimitive(rotor, "Propeller blade B", PrimitiveType.Cube, new Vector3(0, 0.038f, 0), new Vector3(0.055f, 0.025f, 0.32f), accent);
                        rotors.Add(rotor);
                    }
                var scanner = EnsureChild(pivot, "Downward scanner cone"); scanner.localPosition = new Vector3(0, -0.12f, 0);
                if (scanner.GetComponent<MeshFilter>() == null) scanner.gameObject.AddComponent<MeshFilter>();
                if (scanner.GetComponent<MeshRenderer>() == null) scanner.gameObject.AddComponent<MeshRenderer>();
                scanner.GetComponent<MeshFilter>().sharedMesh = scannerMesh; scanner.GetComponent<MeshRenderer>().sharedMaterial = scannerMaterial;
                lights.Add(EnsurePrimitive(pivot, "Drone mode lamp", PrimitiveType.Sphere, new Vector3(0, 0.13f, -0.15f), new Vector3(0.09f, 0.04f, 0.07f), warning).GetComponent<Renderer>());
            }
            var collider = agent.GetComponent<CapsuleCollider>();
            if (collider == null) collider = agent.gameObject.AddComponent<CapsuleCollider>();
            collider.direction = 1; collider.radius = role == AgentRole.GroundVehicle ? 0.36f : 0.38f;
            collider.height = role == AgentRole.GroundVehicle ? 0.72f : 0.24f;
            collider.center = role == AgentRole.GroundVehicle ? new Vector3(0, 0.35f, 0) : Vector3.zero;
            var animation = agent.GetComponent<SwarmRigAnimator>();
            if (animation == null) animation = agent.gameObject.AddComponent<SwarmRigAnimator>();
            animation.Configure(agent, role, pivot, turret, rotors.ToArray(), lights.ToArray());
            if (agent.GetComponent<AgentWorldCallout>() == null) agent.gameObject.AddComponent<AgentWorldCallout>();
        }

        private static void ConfigurePathLine(LineRenderer line, Material material, bool aerial)
        {
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.startWidth = aerial ? 0.095f : 0.12f;
            line.endWidth = aerial ? 0.085f : 0.11f;
            line.startColor = aerial ? new Color(0.52f, 1f, 0.64f, 1f) : new Color(0.12f, 1f, 1f, 1f);
            line.endColor = line.startColor;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
        }

        private static Transform EnsureChild(Transform parent, string name)
        {
            var existing = parent.Find(name); if (existing != null) return existing;
            var child = new GameObject(name); child.transform.SetParent(parent, false); return child.transform;
        }

        private static GameObject EnsurePrimitive(Transform parent, string name, PrimitiveType type, Vector3 position,
            Vector3 scale, Material material, Vector3? euler = null)
        {
            var existing = parent.Find(name);
            if (existing != null)
            {
                existing.localPosition = position; existing.localRotation = Quaternion.Euler(euler ?? Vector3.zero); existing.localScale = scale;
                var existingRenderer = existing.GetComponent<Renderer>(); if (existingRenderer != null) existingRenderer.sharedMaterial = material;
                var existingCollider = existing.GetComponent<Collider>(); if (existingCollider != null) UnityEngine.Object.DestroyImmediate(existingCollider);
                return existing.gameObject;
            }
            var part = GameObject.CreatePrimitive(type); part.name = name; part.transform.SetParent(parent, false);
            part.transform.localPosition = position; part.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero); part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
            var collider = part.GetComponent<Collider>(); if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            Undo.RegisterCreatedObjectUndo(part, "Create " + name); return part;
        }

        private static Material CreateSurfaceMaterial(string name, Color color, bool transparent)
        {
            var path = RootPath + "/Materials/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path); if (existing != null) return existing;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("URP Unlit is required.");
            var material = new Material(shader) { name = name }; material.SetColor("_BaseColor", color);
            if (transparent)
            {
                material.SetFloat("_Surface", 1); material.SetFloat("_Blend", 0); material.SetFloat("_ZWrite", 0);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = (int)RenderQueue.Transparent;
            }
            SaveNew(material, path); return material;
        }

        private static Material CreateLitMaterial(string name, Color color, float metallic, float smoothness, Color emission)
        {
            var path = RootPath + "/Materials/" + name + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit is required for metallic agent materials.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                SaveNew(material, path);
            }
            else if (material.shader != shader) material.shader = shader;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", Mathf.Clamp01(metallic));
            material.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
            material.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0.001f) material.EnableKeyword("_EMISSION");
            else material.DisableKeyword("_EMISSION");
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Mesh ScannerConeMesh()
        {
            const int sides = 24; var path = RootPath + "/Models/ScannerCone.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path); if (existing != null) return existing;
            Directory.CreateDirectory(RootPath + "/Models"); AssetDatabase.Refresh();
            var vertices = new Vector3[sides + 1]; var triangles = new int[sides * 3]; vertices[0] = Vector3.zero;
            for (var i = 0; i < sides; i++)
            {
                var angle = (float)(i * (Math.PI * 2 / sides)); vertices[i + 1] = new Vector3(Mathf.Cos(angle) * 0.5f, -1, Mathf.Sin(angle) * 0.5f);
                var index = i * 3; triangles[index] = 0; triangles[index + 1] = i + 1; triangles[index + 2] = (i + 1) % sides + 1;
            }
            var mesh = new Mesh { name = "Drone downward sensor cone" }; mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateNormals(); SaveNew(mesh, path); return mesh;
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
