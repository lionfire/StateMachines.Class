﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace LionFire.StateMachines.Class
{

    public class BindingProvider<TState, TTransition, TOwner>
    {
        [Flags]
        private enum MemberType
        {
            None = 0,
            Method = 1 << 1,
            Property = 1 << 2,
            Any = Method | Property,
        }

        #region Cache

        Dictionary<string, MethodInfo> methods;
        Dictionary<string, PropertyInfo> properties;

        public void ClearIntermediateCache()
        {
            methods = null;
            properties = null;
        }

        private Dictionary<TTransition, TransitionBinding<TState, TTransition, TOwner>> transitions = new Dictionary<TTransition, TransitionBinding<TState, TTransition, TOwner>>();
        private Dictionary<TState, StateBinding<TState, TTransition, TOwner>> states = new Dictionary<TState, StateBinding<TState, TTransition, TOwner>>();

        #endregion

        #region Static Accessor and Configuration

        public static BindingProvider<TState, TTransition, TOwner> Default { get; set; } = new BindingProvider<TState, TTransition, TOwner>();

        public static StateMachineConventions Conventions
        {
            get => conventions ?? StateMachineConventions.DefaultConventions;
            set => conventions = value;
        }
        private static StateMachineConventions conventions;

        public static BindingFlags MethodBindingFlags { get; set; } = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        public static BindingFlags PropertyBindingFlags { get; set; } = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        #endregion

        #region (Public) Get Methods

        internal virtual StateBinding<TState, TTransition, TOwner> GetStateBinding(TState state)
        {
            if (states.ContainsKey(state)) return states[state];

            var fi = typeof(TState).GetField(state.ToString());
            var aTransition = fi.GetCustomAttribute<StateAttribute>();

            var binding = new StateBinding<TState, TTransition, TOwner>(state)
            {
                // TODO for can enter / leave: also filter based on return type of (bool?)?
                CanEnter = GetHandlerFunc(GetMethod(state, Conventions.CanEnterStatePrefixes, methodParameterTypes: TransitioningHandlerMethodParameterTypes)),
                CanLeave = GetHandlerFunc(GetMethod(state, Conventions.CanLeaveStatePrefixes, methodParameterTypes: TransitioningHandlerMethodParameterTypes)),

                OnEntering = GetHandlerAction(GetMethod(state, Conventions.EnteringStatePrefixes, methodParameterTypes: TransitioningHandlerMethodParameterTypes)),
                OnLeaving = GetHandlerAction(GetMethod(state, Conventions.LeavingStatePrefixes, methodParameterTypes: TransitioningHandlerMethodParameterTypes)),
            };

            states.Add(state, binding);
            return binding;
        }

        //private Action<TOwner, IStateChange<TState, TTransition>> z(Action a)
        //{
        //    return new Action<TOwner, IStateChange<TState, TTransition>>((_owner, _transition) => a()));
        //}

        internal TransitionBinding<TState, TTransition, TOwner> GetTransitionBinding(TTransition transition)
        {
            if (transitions.ContainsKey(transition)) return transitions[transition];

            var fi = typeof(TTransition).GetField(transition.ToString());
            if (fi == null) throw new ArgumentException($"Enum field for {typeof(TTransition).Name} value {transition} not found.");
            var aTransition = fi.GetCustomAttribute<TransitionAttribute>();

            var fromInfo = aTransition.From != null ? GetStateBinding((TState)aTransition.From) : null;
            var toInfo = aTransition.To != null ? GetStateBinding((TState)aTransition.To) : null;

            var binding = new TransitionBinding<TState, TTransition, TOwner>(transition)
            {
                Info = StateMachine<TState, TTransition>.GetTransitionInfo(transition),
                CanTransition = GetHandlerFunc(GetMethod(transition, Conventions.CanTransitionPrefixes)),
                OnTransitioning = GetHandlerAction(GetMethod(transition, Conventions.OnTransitionPrefixes, MemberType.Method)),
                //OnTransitioning = GetHandlerAction(GetMethod(transition, Conventions.OnTransitionPrefixes, MemberType.Method, TransitioningHandlerMethodParameterTypes))
                //?? z(GetHandlerAction(GetMethod(transition, Conventions.OnTransitionPrefixes, MemberType.Method))),
                From = fromInfo,
                To = toInfo,
            };

            transitions.Add(transition, binding);
            return binding;
        }

        #endregion

        //private static readonly Type[] TransitioningHandlerMethodParameterTypes = new Type[] { typeof(TOwner), typeof(StateChange<TState, TTransition, TOwner>) };
        private static readonly Type[] TransitioningHandlerMethodParameterTypes = null; // TODO?  

        #region (Private) Helper Methods

        private MethodInfo GetMethod(TTransition transition, IEnumerable<string> prefixes, MemberType memberType = MemberType.Any, Type[] methodParameterTypes = null)
        {
            return _GetMethod(transition.ToString(), prefixes, memberType, methodParameterTypes);
        }
        private MethodInfo GetMethod(TState state, IEnumerable<string> prefixes, MemberType memberType = MemberType.Any, Type[] methodParameterTypes = null)
        {
            return _GetMethod(state.ToString(), prefixes, memberType, methodParameterTypes);
        }

        private static HashSet<string> DefaultAllowedParameters = new HashSet<string>
        {
            typeof(IStateChange<TState,TTransition>).FullName,
        };

        private MethodInfo _GetMethod(string stateOrTransitionName, IEnumerable<string> prefixes, MemberType memberType = MemberType.Any, Type[] methodParameterTypes = null)
        {
            if (memberType.HasFlag(MemberType.Method))
            {
                methods = null;
                if (methods == null)
                {
                    IEnumerable<MethodInfo> list = typeof(TOwner).GetMethods(MethodBindingFlags);
                    if (methodParameterTypes != null)
                    {
                        list = list.Where(mi =>
                        {
                            var parameters = mi.GetParameters();
                            if (parameters.Length != methodParameterTypes.Length) return false;

                            int i = 0;
                            foreach (var type in methodParameterTypes)
                            {
                                if (type != parameters[i].ParameterType) return false;
                                i++;
                            }
                            return true;
                        });
                    }
                    else
                    {
                        list = list.Where(mi =>
                        {
                            var parameters = mi.GetParameters();
                            if (parameters.Length == 0) return true;
                            if (parameters.Length > 2) return false;

                            var parametersStringList = parameters.Select(p => p.ParameterType.FullName?? p.ParameterType.Name).Aggregate((x, y) => $"{x},{y}");
                            return DefaultAllowedParameters.Contains(parametersStringList);
                        });
                    }
                    methods = list.ToDictionary(fi => fi.Name);
                }

                foreach (var prefix in prefixes)
                {
                    var fieldName = prefix + stateOrTransitionName;
                    if (methods.ContainsKey(fieldName))
                    {
                        return methods[fieldName];
                    }
                }
            }

            if (memberType.HasFlag(MemberType.Property))
            {
                if (properties == null) properties = typeof(TOwner).GetProperties(PropertyBindingFlags).ToDictionary(fi => fi.Name);

                foreach (var prefix in prefixes)
                {
                    var fieldName = prefix + stateOrTransitionName;
                    if (properties.ContainsKey(fieldName) && properties[fieldName].CanRead)
                    {
                        return properties[fieldName].GetGetMethod();
                    }
                }
            }

            return null;
        }

        private Action<TOwner, IStateChange<TState, TTransition>> GetHandlerAction(MethodInfo mi)
        {
            if (mi == null) return null;
            var param = mi.GetParameters();
            if (param.Length == 0)
            {
                return (o, sc) => mi.Invoke(o, Array.Empty<object>());
            }
            else if (param.Length == 1 && param[0].ParameterType == typeof(IStateChange<TState, TTransition>))
            {
                return (o, sc) => mi.Invoke(o, new object[] { sc });
            }
            else
            {
                Debug.WriteLine("[state machine] Unsupported state machine method.  Must have zero parameters, or one IStateChange<TState,TTransition> parameter: " + mi.Name);
                return null;
            }
        }
        private Func<TOwner, IStateChange<TState, TTransition>, bool?> GetHandlerFunc(MethodInfo mi)
        {
            if (mi == null) return null;
            var param = mi.GetParameters();
            if (param.Length == 0)
            {
                return (o, sc) => (bool?)mi.Invoke(o, Array.Empty<object>());
            }
            else if (param.Length == 1 && param[0].ParameterType == typeof(IStateChange<TState, TTransition>))
            {
                return (o, sc) => (bool?)mi.Invoke(o, new object[] { sc });
            }
            else
            {
                Debug.WriteLine("[state machine] Unsupported state machine method.  Must have zero parameters, or one IStateChange<TState,TTransition> parameter: " + mi.Name);
                return null;
            }
        }

        #endregion

    }
}
