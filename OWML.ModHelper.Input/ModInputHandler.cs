﻿using System;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using OWML.Common;
using UnityEngine;
using System.Security.Policy;

namespace OWML.ModHelper.Input
{
    public class ModInputHandler : IModInputHandler
    {
        private const float Cooldown = 0.05f;
        private const float TapDuration = 0.1f;
        private const int MinUsefulKey = 8;
        private const int MaxUsefulKey = 350;
        private const int MaxComboLength = 7;
        private const int GamePadKeyDiff = 20;
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static ModInputHandler Instance { get; private set; }

        private HashSet<IModInputCombination> _singlesPressed = new HashSet<IModInputCombination>();
        private Dictionary<long, IModInputCombination> _comboRegistry = new Dictionary<long, IModInputCombination>();
        private HashSet<InputCommand> _gameBindingRegistry = new HashSet<InputCommand>();
        private HashSet<IModInputCombination> _toResetOnNextFrame = new HashSet<IModInputCombination>();
        private float[] _timeout = new float[MaxUsefulKey];
        private int[] _gameBindingCounter = new int[MaxUsefulKey];
        private IModInputCombination _currentCombination;
        private int _lastSingleUpdate;
        private int _lastCombinationUpdate;
        private readonly IModLogger _logger;
        private readonly IModConsole _console;

        public ModInputHandler(IModLogger logger, IModConsole console, IHarmonyHelper patcher, IOwmlConfig owmlConfig, IModEvents events)
        {
            _console = console;
            _logger = logger;

            var listenerObject = new GameObject("GameBindingsChangeListener");
            var listener = listenerObject.AddComponent<BindingChangeListener>();
            listener.Initialize(this, events);

            if (owmlConfig.BlockInput)
            {
                patcher.AddPostfix<SingleAxisCommand>("UpdateInputCommand", typeof(InputInterceptor), nameof(InputInterceptor.SingleAxisUpdatePost));
                patcher.AddPostfix<DoubleAxisCommand>("UpdateInputCommand", typeof(InputInterceptor), nameof(InputInterceptor.DoubleAxisUpdatePost));
            }
            Instance = this;
        }

        internal bool IsPressedAndIgnored(KeyCode code)
        {
            UpdateCurrentCombination();
            var intKey = (int)code;
            if ((int)code >= MaxUsefulKey)
            {
                intKey -= ((intKey - MaxUsefulKey + GamePadKeyDiff) / GamePadKeyDiff) * GamePadKeyDiff;
            }
            return UnityEngine.Input.GetKey(code) && _currentCombination != null && Time.realtimeSinceStartup - _timeout[intKey] < Cooldown;
        }

        private long? HashFromKeyboard()
        {
            long hash = 0;
            var keysCount = 0;
            var countdownTrigger = true;
            for (var code = MinUsefulKey; code < MaxUsefulKey; code++)
            {
                if (!(Enum.IsDefined(typeof(KeyCode), (KeyCode)code) && UnityEngine.Input.GetKey((KeyCode)code)))
                {
                    continue;
                }
                keysCount++;
                if (keysCount > MaxComboLength)
                {
                    return null;
                }
                hash = hash * MaxUsefulKey + code;
                if (Time.realtimeSinceStartup - _timeout[code] > Cooldown)
                {
                    countdownTrigger = false;
                }
            }
            return countdownTrigger ? -hash : hash;
        }

        private IModInputCombination CombinationFromKeyboard()
        {
            var countdownTrigger = false;
            var nullableHash = HashFromKeyboard();
            if (nullableHash == null)
            {
                return null;
            }
            long hash = (long)nullableHash;
            if (hash < 0)
            {
                countdownTrigger = true;
                hash = -hash;
            }
            if (!_comboRegistry.ContainsKey(hash))
            {
                return null;
            }

            var combination = _comboRegistry[hash];
            if (!(combination == _currentCombination) && countdownTrigger)
            {
                return null;
            }

            if (hash < MaxUsefulKey)
            {
                return combination;
            }
            while (hash > 0)
            {
                _timeout[hash % MaxUsefulKey] = Time.realtimeSinceStartup;
                hash /= MaxUsefulKey;
            }
            return combination;
        }

        private void UpdateCurrentCombination()
        {
            if (_lastCombinationUpdate == Time.frameCount)
            {
                return;
            }
            _lastCombinationUpdate = Time.frameCount;
            foreach (var combo in _toResetOnNextFrame)
            {
                combo.InternalSetPressed(false);
            }
            _toResetOnNextFrame.Clear();
            var combination = CombinationFromKeyboard();
            if (_currentCombination != null && _currentCombination != combination)
            {
                _currentCombination.InternalSetPressed(false);
                _toResetOnNextFrame.Add(_currentCombination);
            }
            if (combination == null)
            {
                _currentCombination = null;
                return;
            }
            _currentCombination = combination;
            _currentCombination.InternalSetPressed();
        }

        public bool IsPressedExact(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            UpdateCurrentCombination();
            return _currentCombination == combination;
        }

        public bool IsNewlyPressedExact(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return IsPressedExact(combination) && combination.IsFirst;
        }

        public bool WasTappedExact(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return !IsPressedExact(combination)
                && (combination.PressDuration < TapDuration)
                && combination.IsFirst;
        }

        public bool WasNewlyReleasedExact(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return !IsPressedExact(combination) && combination.IsFirst;
        }

        private void UpdateSinglesPressed()
        {
            if (_lastSingleUpdate == Time.frameCount)
            {
                return;
            }
            _lastSingleUpdate = Time.frameCount;
            var toRemove = new List<IModInputCombination>();
            foreach (var combo in _singlesPressed)
            {
                if (!IsPressedSingle(combo))
                {
                    toRemove.Add(combo);
                }
                if (!IsPressed(combo))
                {
                    combo.InternalSetPressed(false);
                    _toResetOnNextFrame.Add(combo);
                }
            }
            foreach (var combo in toRemove)
            {
                _singlesPressed.Remove(combo);
            }
        }

        private bool IsPressedSingle(IModInputCombination combination)
        {
            UpdateSinglesPressed();
            if (_currentCombination == combination)
            {
                return true;
            }
            foreach (var key in combination.Singles)
            {
                if (UnityEngine.Input.GetKey(key) && !IsPressedAndIgnored(key))
                {
                    _singlesPressed.Add(combination);
                    combination.InternalSetPressed();
                    return true;
                }
            }
            return false;
        }

        public bool IsPressed(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return IsPressedExact(combination) || IsPressedSingle(combination);
        }

        public bool IsNewlyPressed(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return IsPressed(combination) && combination.IsFirst;
        }

        public bool WasTapped(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return (!IsPressed(combination)) && (combination.PressDuration < TapDuration)
                && combination.IsFirst;
        }

        public bool WasNewlyReleased(IModInputCombination combination)
        {
            if (combination == null)
            {
                return false;
            }
            return (!IsPressed(combination)) && combination.IsFirst;
        }

        private RegistrationCode SwapCombination(IModInputCombination combination, bool toUnregister)
        {
            bool taken = false;
            if (combination.Hashes[0] <= 0)
            {
                return (RegistrationCode)combination.Hashes[0];
            }
            foreach (long hash in combination.Hashes)
            {
                if (toUnregister)
                {
                    _comboRegistry.Remove(hash);
                    continue;
                }
                if (_comboRegistry.ContainsKey(hash) || (hash < MaxUsefulKey && _gameBindingCounter[hash] > 0))
                {
                    taken = true;
                    continue;
                }
                _comboRegistry.Add(hash, combination);
            }
            if (taken)
            {
                return RegistrationCode.CombinationTaken;
            }
            return RegistrationCode.AllNormal;
        }

        private List<string> GetCollisions(ReadOnlyCollection<long> hashes)
        {
            List<string> combos = new List<string>();
            foreach (long hash in hashes)
            {
                if (_comboRegistry.ContainsKey(hash))
                {
                    combos.Add(_comboRegistry[hash].FullName);
                }
                if (hash < MaxUsefulKey && _gameBindingCounter[hash] > 0)
                {
                    combos.Add("Outer Wilds." + Enum.GetName(typeof(KeyCode), (KeyCode)hash));
                }
            }
            return combos;
        }

        public IModInputCombination RegisterCombination(IModBehaviour mod, string name, string combination)
        {
            var combo = new ModInputCombination(mod.ModHelper.Manifest, name, combination);
            switch (SwapCombination(combo, false))
            {
                case RegistrationCode.InvalidCombination:
                    _console.WriteLine($"Failed to register \"{combo.FullName}\": invalid combination!");
                    return null;
                case RegistrationCode.CombinationTooLong:
                    _console.WriteLine($"Failed to register \"{combo.FullName}\": too long!");
                    return null;
                case RegistrationCode.CombinationTaken:
                    _console.WriteLine($"Failed to register \"{combo.FullName}\": already in use by following mods:");
                    var collisions = GetCollisions(combo.Hashes);
                    foreach (string collision in collisions)
                    {
                        _console.WriteLine($"\"{collision}\"");
                    }
                    return null;
                case RegistrationCode.AllNormal:
                    return combo;
                default:
                    return null;
            }
        }

        public void UnregisterCombination(IModInputCombination combination)
        {
            if (combination == null)
            {
                _console.WriteLine("Failed to unregister: null combination!");
                return;
            }
            switch (SwapCombination(combination, true))
            {
                case RegistrationCode.InvalidCombination:
                    _console.WriteLine($"Failed to unregister \"{combination.FullName}\": invalid combination!");
                    return;
                case RegistrationCode.CombinationTooLong:
                    _console.WriteLine($"Failed to unregister \"{combination.FullName}\": too long!");
                    return;
                case RegistrationCode.AllNormal:
                    _logger.Log($"succesfully unregistered \"{combination.FullName}\"");
                    return;
                default:
                    return;
            }
        }

        internal void SwapGamesBinding(InputCommand binding, bool toUnregister)
        {
            if ((_gameBindingRegistry.Contains(binding) ^ toUnregister) || binding == null)
            {
                return;
            }
            var fields = binding is SingleAxisCommand ?
                typeof(SingleAxisCommand).GetFields(NonPublic) : typeof(DoubleAxisCommand).GetFields(NonPublic);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(List<KeyCode>))
                {
                    var keys = (List<KeyCode>)(field.GetValue(binding));
                    foreach (var key in keys)
                    {
                        if (key != KeyCode.None)
                        {
                            var intKey = (int)key;
                            if ((int)key >= MaxUsefulKey)
                            {
                                intKey -= ((intKey - MaxUsefulKey + GamePadKeyDiff) / GamePadKeyDiff) * GamePadKeyDiff;
                            }
                            _gameBindingCounter[intKey] += toUnregister ? -1 : 1;
                        }
                    }
                }
            }
            if (toUnregister)
            {
                _gameBindingRegistry.Remove(binding);
            }
            else
            {
                _gameBindingRegistry.Add(binding);
            }
        }

        internal void RegisterGamesBinding(InputCommand binding)
        {
            SwapGamesBinding(binding, false);
        }

        internal void UnregisterGamesBinding(InputCommand binding)
        {
            SwapGamesBinding(binding, true);
        }

        internal void UpdateGamesBindings()
        {
            Array.ForEach<int>(_gameBindingCounter, x => x = 0);
            _gameBindingRegistry.Clear();
            var inputCommands = typeof(InputLibrary).GetFields(BindingFlags.Public | BindingFlags.Static);
            Array.ForEach<FieldInfo>(inputCommands, field => RegisterGamesBinding(field.GetValue(null) as InputCommand));
        }
    }
}