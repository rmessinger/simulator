﻿/**
 * Copyright (c) 2019 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */

using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Database;
using Web.Modules;
using Nancy.Hosting.Self;


namespace Web
{
    public class Loader : MonoBehaviour
    {
        private int Port = 8080;
        private string Address;
        private NancyHost Server;

        // TODO: create a separate class with static configuration
        public static string ApplicationRoot { get; private set; }

        public Button button;
        public SimulatorManager SimulatorManager;

        // NOTE: When simulation is not running this reference will be null.
        public Simulation CurrentSimulation;

        ConcurrentQueue<Action> Actions = new ConcurrentQueue<Action>();
        string LoaderScene;

        // Loader object is never destroyed, even between scene reloads
        public static Loader Instance { get; private set; }

        void Start()
        {
            if (Instance != null)
            {
                InitLoader();
                Destroy(gameObject);
                return;
            }

            var info = Resources.Load<Simulator.Utilities.BuildInfo>("BuildInfo");
            if (info != null)
            {
                // TODO: probably show this somewhere in UI
                var timestamp = DateTime.ParseExact(info.Timestamp, "o", CultureInfo.InvariantCulture);
                Debug.Log($"Timestamp = {timestamp}");
                Debug.Log($"Version = {info.Version}");
                Debug.Log($"GitCommitId = {info.GitCommitId}");
                Debug.Log($"GitBranchName = {info.GitBranchName}");
            }

            ApplicationRoot = Path.Combine(Application.dataPath, "..");
            var path = Path.Combine(Application.persistentDataPath, "data.db");
            DatabaseManager.Init($"Data Source = {path};version=3;");

            Address = $"http://localhost:{Port}";

            // Bind to all interfaces instead of localhost
            var config = new HostConfiguration { RewriteLocalhost = true };

            Server = new NancyHost(new MyBootstrapper(), config, new Uri(Address));
            Server.Start();
            DownloadManager.Init();

            LoaderScene = SceneManager.GetActiveScene().name;

            DontDestroyOnLoad(this);
            Instance = this;
            InitLoader();
        }

        void InitLoader()
        {
            button.onClick.AddListener(Instance.OnButtonClick);
        }

        void OnButtonClick()
        {
            Application.OpenURL(Address + "/");
        }

        void OnApplicationQuit()
        {
            Server?.Stop();
        }

        private void Update()
        {
            Action action;
            while (Actions.TryDequeue(out action))
            {
                action();
            }
        }

        public static void StartAsync(Simulation simulation)
        {
            Debug.Assert(Instance.CurrentSimulation == null);
            Instance.Actions.Enqueue(() =>
            {
                using (var db = DatabaseManager.Open())
                {
                    AssetBundle mapBundle = null;
                    try
                    {
                        simulation.Status = "Starting";
                        NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));

                        // TODO: this should probably change to pass only necessary information to place where it is needed
                        var config = new ConfigData()
                        {
                            Id = simulation.Id,
                            Name = simulation.Name,
                            Cluster = db.Single<Cluster>(simulation.Cluster).Ips,
                            ApiOnly = simulation.ApiOnly.GetValueOrDefault(),
                            Interactive = simulation.Interactive.GetValueOrDefault(),
                            OffScreen = simulation.OffScreen.GetValueOrDefault(),
                            TimeOfDay = simulation.TimeOfDay.GetValueOrDefault(),
                            Rain = simulation.Rain.GetValueOrDefault(),
                            Fog = simulation.Fog.GetValueOrDefault(),
                            Wetness = simulation.Wetness.GetValueOrDefault(),
                            Cloudiness = simulation.Cloudiness.GetValueOrDefault(),
                        };

                        // load environment
                        {
                            var mapBundlePath = db.Single<Map>(simulation.Map).LocalPath;

                            // TODO: make this async
                            mapBundle = AssetBundle.LoadFromFile(mapBundlePath);
                            if (mapBundle == null)
                            {
                                throw new Exception($"Failed to load environment from '{mapBundlePath}' asset bundle");
                            }

                            var scenes = mapBundle.GetAllScenePaths();
                            if (scenes.Length != 1)
                            {
                                throw new Exception($"Unsupported environment in '{mapBundlePath}' asset bundle, only 1 scene expected");
                            }

                            var sceneName = Path.GetFileNameWithoutExtension(scenes[0]);

                            var loader = SceneManager.LoadSceneAsync(sceneName);
                            loader.completed += op =>
                            {
                                if (op.isDone)
                                {
                                    mapBundle.Unload(false);
                                    SetupScene(config, simulation);
                                }
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.Log($"Failed to start simulation with {simulation.Id}");
                        Debug.LogException(ex);

                        // NOTE: In case of failure we have to update Simulation state
                        simulation.Status = "Invalid";
                        db.Update(simulation);

                        if (SceneManager.GetActiveScene().name != Instance.LoaderScene)
                        {
                            SceneManager.LoadScene(Instance.LoaderScene);
                        }
                        mapBundle?.Unload(false);
                        AssetBundle.UnloadAllAssetBundles(true);
                        Instance.CurrentSimulation = null;

                        // TODO: take ex.Message and append it to response here
                        NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));
                    }
                }
            });
        }

        public static void StopAsync()
        {
            Debug.Assert(Instance.CurrentSimulation != null);
            Instance.Actions.Enqueue(() =>
            {
                var simulation = Instance.CurrentSimulation;
                using (var db = DatabaseManager.Open())
                {
                    try
                    {
                        simulation.Status = "Stopping";
                        NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));

                        var loader = SceneManager.LoadSceneAsync(Instance.LoaderScene);
                        loader.completed += op =>
                        {
                            if (op.isDone)
                            {
                                AssetBundle.UnloadAllAssetBundles(true);

                                simulation.Status = "Valid";
                                NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));
                                Instance.CurrentSimulation = null;
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        Debug.Log($"Failed to stop simulation with {simulation.Id}");
                        Debug.LogException(ex);

                        // NOTE: In case of failure we have to update Simulation state
                        simulation.Status = "Invalid";
                        db.Update(simulation);

                        // TODO: take ex.Message and append it to response here
                        NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));
                    }
                }
            });
        }

        static void SetupScene(ConfigData config, Simulation simulation)
        {
            using (var db = DatabaseManager.Open())
            {
                try
                {
                    if (simulation.Vehicles == null)
                    {
                        config.AgentPrefabs = Array.Empty<GameObject>();
                    }
                    else
                    {
                        var vehiclesBundlePath = simulation.Vehicles.Split(',').Select(v => db.SingleOrDefault<Vehicle>(Convert.ToInt32(v)).LocalPath);

                        var prefabs = new List<GameObject>();
                        foreach (var vehicleBundlePath in vehiclesBundlePath)
                        {
                            // TODO: make this async
                            var vehicleBundle = AssetBundle.LoadFromFile(vehicleBundlePath);
                            if (vehicleBundle != null)
                            {
                                throw new Exception($"Failed to load vehicle from '{vehicleBundlePath}' asset bundle");
                            }
                            try
                            {

                                var vehicleAssets = vehicleBundle.GetAllAssetNames();
                                if (vehicleAssets.Length != 1)
                                {
                                    throw new Exception($"Unsupported vehicle in '{vehicleBundlePath}' asset bundle, only 1 asset expected");
                                }

                                // TODO: make this async
                                var prefab = vehicleBundle.LoadAsset<GameObject>(vehicleAssets[0]);
                                prefabs.Add(prefab);
                            }
                            finally
                            {
                                vehicleBundle.Unload(false);
                            }
                        }

                        config.AgentPrefabs = prefabs.ToArray();
                    }

                    // simulation manager
                    {
                        var sim = Instantiate(Instance.SimulatorManager);
                        sim.name = "SimulatorManager";
                        sim.Init(config);
                    }

                    // ready to go!
                    Instance.CurrentSimulation = simulation;
                    Instance.CurrentSimulation.Status = "Running";
                    NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));
                }
                catch (Exception ex)
                {
                    Debug.Log($"Failed to start simulation with {simulation.Id}");
                    Debug.LogException(ex);

                    // NOTE: In case of failure we have to update Simulation state
                    simulation.Status = "Invalid";
                    db.Update(simulation);

                    // TODO: take ex.Message and append it to response here
                    NotificationManager.SendNotification("simulation", SimulationModule.ConvertSimToResponse(simulation));

                    if (SceneManager.GetActiveScene().name != Instance.LoaderScene)
                    {
                        SceneManager.LoadScene(Instance.LoaderScene);
                        AssetBundle.UnloadAllAssetBundles(true);
                        Instance.CurrentSimulation = null;
                    }
                }
            }
        }
    }
}