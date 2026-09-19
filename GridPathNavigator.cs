using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overcooked2DishwasherBot
{
    internal sealed class GridPathNavigator
    {
        private const float MaximumWalkableHeightDifference = 0.45f;

        private static readonly GridIndex[] FourWayOffsets =
        {
            new GridIndex(1, 0, 0),
            new GridIndex(-1, 0, 0),
            new GridIndex(0, 0, 1),
            new GridIndex(0, 0, -1)
        };

        private readonly List<Vector3> _worldPath = new List<Vector3>();
        private readonly HashSet<GridIndex> _rejectedInteractionCells = new HashSet<GridIndex>(default(GridIndex));
        private readonly HashSet<GridEdge> _blockedPathEdges = new HashSet<GridEdge>();
        private readonly HashSet<GridIndex> _dynamicBlockedCells = new HashSet<GridIndex>(default(GridIndex));
        private GameObject _target;
        private GridManager _grid;
        private GridIndex _currentGoal;
        private bool _hasCurrentGoal;
        private bool _searchAllNeighbourCells;
        private int _pathCursor;
        private float _nextRepathTime;
        private Vector3 _lastPlayerPosition;
        private float _stuckSince;
        private bool _hasPath;
        private bool _avoidanceMode;
        private Vector3 _avoidDirection;
        private float _avoidUntil;
        private int _brakingPathCursor = -1;
        private Vector3 _plannedTargetPosition;
        private Vector3 _pathStartPoint;
        private Vector3 _routeBuildPosition;
        private Vector3 _meaningfulProgressPosition;
        private Vector3 _interactionFacingDirection;
        private GameObject _facingTarget;
        private float _facingBurstUntil;
        private float _dashRepathUntil;
        private float _meaningfulProgressTime;
        private float _dashSuppressedUntil;
        private float _chefAvoidanceRadius;

        internal string Status { get; private set; }
        internal bool CanDash { get; private set; }
        internal bool CanExtremeDash { get; private set; }

        internal void SetChefAvoidanceRadius(float radius)
        {
            radius = Mathf.Max(0f, radius);
            if (Mathf.Abs(radius - _chefAvoidanceRadius) < 0.01f)
            {
                return;
            }

            _chefAvoidanceRadius = radius;
            _nextRepathTime = 0f;
        }

        internal void Clear()
        {
            _worldPath.Clear();
            _target = null;
            _grid = null;
            _pathCursor = 0;
            _nextRepathTime = 0f;
            _stuckSince = 0f;
            _hasPath = false;
            _avoidanceMode = false;
            _hasCurrentGoal = false;
            _searchAllNeighbourCells = false;
            _rejectedInteractionCells.Clear();
            _blockedPathEdges.Clear();
            _dynamicBlockedCells.Clear();
            _avoidDirection = Vector3.zero;
            _avoidUntil = 0f;
            _brakingPathCursor = -1;
            _plannedTargetPosition = Vector3.zero;
            _pathStartPoint = Vector3.zero;
            _routeBuildPosition = Vector3.zero;
            _meaningfulProgressPosition = Vector3.zero;
            _interactionFacingDirection = Vector3.zero;
            _facingTarget = null;
            _facingBurstUntil = 0f;
            _dashRepathUntil = 0f;
            _meaningfulProgressTime = 0f;
            _dashSuppressedUntil = 0f;
            CanDash = false;
            CanExtremeDash = false;
            Status = "cleared";
        }

        internal Vector3 DirectionTo(PlayerControls player, GameObject target, out bool atInteractionCell)
        {
            return DirectionTo(player, target, false, out atInteractionCell);
        }

        internal Vector3 DirectionTo(
            PlayerControls player,
            GameObject target,
            bool searchAllNeighbourCells,
            out bool atInteractionCell)
        {
            atInteractionCell = false;
            CanDash = false;
            CanExtremeDash = false;
            if (player == null || target == null)
            {
                return Vector3.zero;
            }

            Vector3 playerPosition = player.transform.position;
            Vector3 targetPosition = target.transform.position;
            Vector3 direct = Flatten(targetPosition - playerPosition);

            bool moved = Flatten(playerPosition - _lastPlayerPosition).sqrMagnitude > 0.015f * 0.015f;
            if (moved)
            {
                _lastPlayerPosition = playerPosition;
                _stuckSince = Time.time;
            }
            else if (_stuckSince <= 0f)
            {
                _stuckSince = Time.time;
            }

            bool targetChanged = _avoidanceMode
                || _target != target
                || _searchAllNeighbourCells != searchAllNeighbourCells;
            bool targetMoved = !targetChanged
                && Flatten(targetPosition - _plannedTargetPosition).sqrMagnitude > 0.4f * 0.4f;
            if (targetChanged || targetMoved)
            {
                _rejectedInteractionCells.Clear();
                _blockedPathEdges.Clear();
                _hasCurrentGoal = false;
                _searchAllNeighbourCells = searchAllNeighbourCells;
                _brakingPathCursor = -1;
                _interactionFacingDirection = Vector3.zero;
                _facingTarget = null;
                _facingBurstUntil = 0f;
            }
            if (targetChanged
                || targetMoved
                || _meaningfulProgressTime <= 0f
                || Flatten(playerPosition - _meaningfulProgressPosition).sqrMagnitude >= 0.25f * 0.25f)
            {
                _meaningfulProgressPosition = playerPosition;
                _meaningfulProgressTime = Time.time;
            }
            bool stuck = Time.time - _stuckSince > 1.25f;
            float distanceRepathThreshold = Time.time < _dashRepathUntil ? 0.45f : 1f;
            bool movedSinceRouteBuild = _hasPath
                && Flatten(playerPosition - _routeBuildPosition).sqrMagnitude
                    >= distanceRepathThreshold * distanceRepathThreshold;
            if (targetChanged
                || targetMoved
                || movedSinceRouteBuild
                || Time.time >= _nextRepathTime
                || stuck)
            {
                _target = target;
                BuildPath(player, target, searchAllNeighbourCells);
                float normalRepathInterval = _chefAvoidanceRadius > 0f ? 0.3f : 0.9f;
                _nextRepathTime = Time.time + (stuck ? 0.35f : (_hasPath ? normalRepathInterval : 0.25f));
                if (stuck)
                {
                    _stuckSince = Time.time;
                }
            }

            if (AdvanceReachedWaypoints(player))
            {
                return Vector3.zero;
            }

            if (_pathCursor >= _worldPath.Count)
            {
                if (!_hasPath)
                {
                    Status = "no reachable adjacent cell";
                    return Vector3.zero;
                }
                atInteractionCell = true;
                Status = "at interaction cell";
                return FinalApproachDirection(player, target, GetFacingDirection(direct));
            }

            Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
            if (_pathCursor == _worldPath.Count - 1 && toWaypoint.sqrMagnitude < 0.35f * 0.35f)
            {
                atInteractionCell = true;
                Status = "approaching target from final cell";
                return FinalApproachDirection(player, target, GetFacingDirection(direct));
            }

            Status = "following waypoint " + (_pathCursor + 1) + "/" + _worldPath.Count;
            // While travelling between grid cells, do not exempt the eventual target's
            // colliders. Only the final facing/nudge step may intentionally touch it.
            if (RejectPhysicallyBlockedPathEdge(player, toWaypoint.normalized))
            {
                return Vector3.zero;
            }
            if (HasOtherChefAhead(player, toWaypoint.normalized, 0.75f))
            {
                _nextRepathTime = 0f;
                Status = "chef blocking route; requesting alternate path";
                return Vector3.zero;
            }
            if (Time.time - _meaningfulProgressTime >= 0.5f)
            {
                _dashSuppressedUntil = Time.time + 1.5f;
                BlockCurrentRouteEdge(player);
                _nextRepathTime = 0f;
                _meaningfulProgressPosition = playerPosition;
                _meaningfulProgressTime = Time.time;
                Status = "no route progress; suppressing dash and replanning";
                return Vector3.zero;
            }
            CanDash = HasSafeDashRun(player, toWaypoint.normalized);
            CanExtremeDash = HasExtremeDashRun(player, toWaypoint.normalized);
            return SafeRouteDirection(player, toWaypoint.normalized);
        }

        internal void NotifyDashStarted(float dashDuration)
        {
            bool enteringDashRepathWindow = Time.time >= _dashRepathUntil;
            _dashRepathUntil = Mathf.Max(
                _dashRepathUntil,
                Time.time + Mathf.Max(0.35f, dashDuration));
            if (enteringDashRepathWindow)
            {
                _nextRepathTime = 0f;
            }
        }

        internal Vector3 DirectionAwayFrom(
            PlayerControls player,
            IList<Vector3> threatPositions,
            float safeDistance,
            out bool reachedAvoidanceGoal)
        {
            reachedAvoidanceGoal = false;
            CanDash = false;
            CanExtremeDash = false;
            if (player == null || threatPositions == null || threatPositions.Count == 0)
            {
                return Vector3.zero;
            }

            Vector3 playerPosition = player.transform.position;
            bool moved = Flatten(playerPosition - _lastPlayerPosition).sqrMagnitude > 0.015f * 0.015f;
            if (moved)
            {
                _lastPlayerPosition = playerPosition;
                _stuckSince = Time.time;
            }
            else if (_stuckSince <= 0f)
            {
                _stuckSince = Time.time;
            }

            if (!_avoidanceMode)
            {
                _worldPath.Clear();
                _pathCursor = 0;
                _target = null;
                _hasPath = false;
                _hasCurrentGoal = false;
                _avoidanceMode = true;
                _nextRepathTime = 0f;
            }

            bool stuck = Time.time - _stuckSince > 0.9f;
            if (Time.time >= _nextRepathTime || stuck)
            {
                BuildAvoidancePath(player, threatPositions, safeDistance);
                _nextRepathTime = Time.time + (stuck ? 0.15f : 0.3f);
                if (stuck)
                {
                    _stuckSince = Time.time;
                }
            }

            while (_pathCursor < _worldPath.Count
                && Flatten(_worldPath[_pathCursor] - playerPosition).sqrMagnitude < 0.32f * 0.32f)
            {
                _pathCursor++;
            }

            if (_pathCursor < _worldPath.Count)
            {
                Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
                Status = "avoiding player via waypoint " + (_pathCursor + 1) + "/" + _worldPath.Count;
                return SafeRouteDirection(player, toWaypoint.normalized);
            }

            Vector3 fallback = CalculateRepulsion(playerPosition, threatPositions, player.transform.forward);
            if (!_hasPath && fallback.sqrMagnitude > 0.001f)
            {
                Status = "no grid escape path; using safe repulsion";
                return SafeDirection(player, null, fallback.normalized);
            }

            reachedAvoidanceGoal = _hasPath;
            Status = "at avoidance goal";
            return Vector3.zero;
        }

        internal bool RejectCurrentInteractionCell()
        {
            if (!_hasCurrentGoal)
            {
                return false;
            }

            _rejectedInteractionCells.Add(_currentGoal);
            _worldPath.Clear();
            _pathCursor = 0;
            _hasPath = false;
            _hasCurrentGoal = false;
            _nextRepathTime = 0f;
            _avoidDirection = Vector3.zero;
            _avoidUntil = 0f;
            _brakingPathCursor = -1;
            _interactionFacingDirection = Vector3.zero;
            _facingTarget = null;
            _facingBurstUntil = 0f;
            Status = "interaction cell rejected";
            return true;
        }

        private void BuildPath(PlayerControls player, GameObject target, bool searchAllNeighbourCells)
        {
            bool preferCurrentGoal = _hasCurrentGoal
                && !_rejectedInteractionCells.Contains(_currentGoal);
            GridIndex preferredGoal = _currentGoal;
            _worldPath.Clear();
            _pathCursor = 0;
            _hasPath = false;
            _hasCurrentGoal = false;
            _avoidanceMode = false;
            _brakingPathCursor = -1;
            _interactionFacingDirection = Vector3.zero;
            _plannedTargetPosition = target.transform.position;
            _routeBuildPosition = player.transform.position;

            GridManager playerGrid = GameUtils.GetGridManager(player.transform);
            StaticGridLocation registeredTargetLocation = FindRegisteredGridLocation(target);
            if (playerGrid == null)
            {
                _grid = null;
                Status = "player has no navigation grid";
                return;
            }

            _grid = playerGrid;
            GridIndex start = _grid.GetUnclampedGridLocationFromPos(player.transform.position);
            float walkingSurfaceY = GetWalkingSurfaceY(player);
            CollectDynamicBlockedCells(player, start, walkingSurfaceY, true);
            // Some kitchens register counters and stations on local GridManager instances
            // even though chefs can walk between them on one continuous floor. A grid ID
            // mismatch therefore does not mean the target is physically unreachable.
            // Reuse a registered index only when it belongs to the player's current grid;
            // otherwise project the target's world position onto the walking grid.
            GridIndex rawTargetIndex = registeredTargetLocation != null
                && registeredTargetLocation.AccessGridManager == playerGrid
                    ? registeredTargetLocation.GridIndex
                    : _grid.GetUnclampedGridLocationFromPos(target.transform.position);

            // A stack on a worktop is often one grid level above the chef. This search only
            // expands X/Z, so its goal must stay on the chef's current walking layer.
            GridIndex targetIndex = new GridIndex(rawTargetIndex.X, start.Y, rawTargetIndex.Z);
            Point3 halfSize = _grid.GetGridHalfSize();
            _pathStartPoint = _grid.GetPosFromGridLocation(start);

            List<GridIndex> goals = new List<GridIndex>();
            if (searchAllNeighbourCells)
            {
                for (int x = -1; x <= 1; x++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && z == 0)
                        {
                            continue;
                        }
                        GridIndex goal = targetIndex + new GridIndex(x, 0, z);
                        if (!_rejectedInteractionCells.Contains(goal)
                            && Inside(goal, halfSize)
                            && IsWalkable(goal, start, walkingSurfaceY))
                        {
                            goals.Add(goal);
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < FourWayOffsets.Length; i++)
                {
                    GridIndex goal = targetIndex + FourWayOffsets[i];
                    if (!_rejectedInteractionCells.Contains(goal)
                        && Inside(goal, halfSize)
                        && IsWalkable(goal, start, walkingSurfaceY))
                    {
                        goals.Add(goal);
                    }
                }
            }

            if (goals.Count == 0)
            {
                if (_rejectedInteractionCells.Count > 0)
                {
                    // Every currently reachable side may have been rejected by the
                    // game's interaction scan. Start a fresh cycle instead of leaving
                    // the chef permanently idle beside the station.
                    _rejectedInteractionCells.Clear();
                    BuildPath(player, target, searchAllNeighbourCells);
                    return;
                }
                Status = "target has no walkable adjacent cell on player layer Y=" + start.Y;
                return;
            }

            preferCurrentGoal = preferCurrentGoal && goals.Contains(preferredGoal);

            List<GridIndex> best = null;
            GridIndex bestGoal = default(GridIndex);
            for (int i = 0; i < goals.Count; i++)
            {
                List<GridIndex> path = FindPath(start, goals[i], halfSize, walkingSurfaceY);
                if (path != null
                    && (best == null
                        || (preferCurrentGoal && goals[i] == preferredGoal)
                        || (!preferCurrentGoal && path.Count < best.Count)))
                {
                    best = path;
                    bestGoal = goals[i];
                    if (preferCurrentGoal && goals[i] == preferredGoal)
                    {
                        break;
                    }
                }
            }

            if (best == null)
            {
                // A learned edge may leave a genuinely narrow area with no alternate
                // route. Forget it for the next (non-dashing) attempt rather than making
                // the target permanently unreachable.
                _blockedPathEdges.Clear();
                Status = "path search failed on player layer Y=" + start.Y;
                return;
            }

            _hasPath = true;
            _currentGoal = bestGoal;
            _hasCurrentGoal = true;
            _interactionFacingDirection = Flatten(
                _grid.GetPosFromGridLocation(targetIndex)
                - _grid.GetPosFromGridLocation(bestGoal));
            Status = "path built with " + best.Count + " waypoint(s) on player layer Y=" + start.Y;

            for (int i = 0; i < best.Count; i++)
            {
                Vector3 point = _grid.GetPosFromGridLocation(best[i]);
                RaycastHit hit;
                if (TryFindGround(point, walkingSurfaceY, out hit))
                {
                    point.y = hit.point.y;
                }
                _worldPath.Add(point);
            }
        }

        private void BuildAvoidancePath(
            PlayerControls player,
            IList<Vector3> threatPositions,
            float safeDistance)
        {
            _worldPath.Clear();
            _pathCursor = 0;
            _hasPath = false;
            _hasCurrentGoal = false;
            _brakingPathCursor = -1;

            _grid = GameUtils.GetGridManager(player.transform);
            if (_grid == null)
            {
                Status = "avoidance has no player grid";
                return;
            }

            Vector3 playerPosition = player.transform.position;
            float walkingSurfaceY = GetWalkingSurfaceY(player);
            GridIndex start = _grid.GetUnclampedGridLocationFromPos(playerPosition);
            Point3 halfSize = _grid.GetGridHalfSize();
            // The bot may begin avoidance inside the configured clearance radius. Only
            // the chefs' occupied cells are blocked here so an outward route can be built.
            CollectDynamicBlockedCells(player, start, walkingSurfaceY, false);
            _pathStartPoint = _grid.GetPosFromGridLocation(start);
            int maxDepth = Mathf.Clamp(Mathf.CeilToInt(safeDistance) + 2, 3, 6);
            float safeDistanceSquared = safeDistance * safeDistance;
            Vector3 preferredDirection = CalculateRepulsion(playerPosition, threatPositions, player.transform.forward);

            Queue<GridIndex> open = new Queue<GridIndex>();
            Dictionary<GridIndex, GridIndex> parent = new Dictionary<GridIndex, GridIndex>(default(GridIndex));
            Dictionary<GridIndex, int> depth = new Dictionary<GridIndex, int>(default(GridIndex));
            HashSet<GridIndex> visited = new HashSet<GridIndex>(default(GridIndex));
            open.Enqueue(start);
            visited.Add(start);
            depth.Add(start, 0);

            bool foundSafe = false;
            bool foundFallback = false;
            GridIndex bestGoal = default(GridIndex);
            int bestDepth = int.MaxValue;
            float bestSafeScore = float.NegativeInfinity;
            float bestFallbackScore = float.NegativeInfinity;

            int safety = 0;
            while (open.Count > 0 && safety++ < 10000)
            {
                GridIndex current = open.Dequeue();
                int currentDepth = depth[current];
                if (current != start)
                {
                    Vector3 worldPosition = _grid.GetPosFromGridLocation(current);
                    float minimumDistanceSquared = MinimumHorizontalSqrDistance(worldPosition, threatPositions);
                    Vector3 fromPlayer = Flatten(worldPosition - playerPosition);
                    float alignment = fromPlayer.sqrMagnitude > 0.001f
                        ? Vector3.Dot(fromPlayer.normalized, preferredDirection)
                        : -1f;

                    if (minimumDistanceSquared >= safeDistanceSquared)
                    {
                        float safeScore = minimumDistanceSquared + alignment * 0.35f;
                        if (!foundSafe
                            || currentDepth < bestDepth
                            || (currentDepth == bestDepth && safeScore > bestSafeScore))
                        {
                            foundSafe = true;
                            bestGoal = current;
                            bestDepth = currentDepth;
                            bestSafeScore = safeScore;
                        }
                    }
                    else if (!foundSafe)
                    {
                        float fallbackScore = minimumDistanceSquared + alignment * 0.25f - currentDepth * 0.08f;
                        if (!foundFallback || fallbackScore > bestFallbackScore)
                        {
                            foundFallback = true;
                            bestGoal = current;
                            bestFallbackScore = fallbackScore;
                        }
                    }
                }

                if (currentDepth >= maxDepth || (foundSafe && currentDepth >= bestDepth))
                {
                    continue;
                }

                for (int i = 0; i < FourWayOffsets.Length; i++)
                {
                    GridIndex next = current + FourWayOffsets[i];
                    if (!Inside(next, halfSize)
                        || visited.Contains(next)
                        || !IsWalkable(next, start, walkingSurfaceY))
                    {
                        continue;
                    }

                    visited.Add(next);
                    parent.Add(next, current);
                    depth.Add(next, currentDepth + 1);
                    open.Enqueue(next);
                }
            }

            if (!foundSafe && !foundFallback)
            {
                Status = "avoidance found no reachable cell";
                return;
            }

            List<GridIndex> path = new List<GridIndex>();
            GridIndex cursor = bestGoal;
            while (cursor != start)
            {
                path.Add(cursor);
                cursor = parent[cursor];
            }
            path.Reverse();

            for (int i = 0; i < path.Count; i++)
            {
                Vector3 point = _grid.GetPosFromGridLocation(path[i]);
                RaycastHit hit;
                if (TryFindGround(point, walkingSurfaceY, out hit))
                {
                    point.y = hit.point.y;
                }
                _worldPath.Add(point);
            }

            _hasPath = _worldPath.Count > 0;
            Status = foundSafe
                ? "avoidance path built to a safe cell"
                : "avoidance path built toward the safest reachable cell";
        }

        private bool AdvanceReachedWaypoints(PlayerControls player)
        {
            Vector3 playerPosition = player.transform.position;
            while (_pathCursor < _worldPath.Count)
            {
                Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
                float distance = toWaypoint.magnitude;
                bool mustStop = MustStopAtWaypoint(_pathCursor);

                if (_pathCursor < _worldPath.Count - 1
                    && HasPassedWaypoint(playerPosition, _pathCursor))
                {
                    _brakingPathCursor = -1;
                    _pathCursor++;
                    continue;
                }

                if (_brakingPathCursor == _pathCursor)
                {
                    if (HorizontalSpeed(player) > 0.85f)
                    {
                        Status = "braking before route turn";
                        return true;
                    }

                    _brakingPathCursor = -1;
                    if (distance <= 0.45f)
                    {
                        _pathCursor++;
                        continue;
                    }
                }

                if (mustStop && distance <= 0.38f && HorizontalSpeed(player) > 1.15f)
                {
                    _brakingPathCursor = _pathCursor;
                    Status = _pathCursor == _worldPath.Count - 1
                        ? "braking at interaction cell"
                        : "braking before route turn";
                    return true;
                }

                float reachedDistance = mustStop ? 0.24f : 0.38f;
                if (distance >= reachedDistance)
                {
                    return false;
                }

                _pathCursor++;
            }
            return false;
        }

        private bool HasPassedWaypoint(Vector3 playerPosition, int cursor)
        {
            Vector3 segmentStart = cursor == 0 ? _pathStartPoint : _worldPath[cursor - 1];
            Vector3 segment = Flatten(_worldPath[cursor] - segmentStart);
            if (segment.sqrMagnitude < 0.001f)
            {
                return true;
            }

            Vector3 fromStart = Flatten(playerPosition - segmentStart);
            Vector3 direction = segment.normalized;
            float along = Vector3.Dot(fromStart, direction);
            Vector3 lateral = fromStart - direction * along;
            return along >= segment.magnitude && lateral.sqrMagnitude <= 0.55f * 0.55f;
        }

        private bool MustStopAtWaypoint(int cursor)
        {
            if (cursor < 0 || cursor >= _worldPath.Count)
            {
                return false;
            }
            if (cursor == _worldPath.Count - 1)
            {
                return true;
            }

            Vector3 previous = cursor == 0 ? _pathStartPoint : _worldPath[cursor - 1];
            Vector3 incoming = Flatten(_worldPath[cursor] - previous);
            Vector3 outgoing = Flatten(_worldPath[cursor + 1] - _worldPath[cursor]);
            if (incoming.sqrMagnitude < 0.001f || outgoing.sqrMagnitude < 0.001f)
            {
                return false;
            }
            return Vector3.Dot(incoming.normalized, outgoing.normalized) < 0.75f;
        }

        private static float HorizontalSpeed(PlayerControls player)
        {
            return player == null || player.Motion == null
                ? 0f
                : player.Motion.GetVelocityXZ().magnitude;
        }

        private Vector3 GetFacingDirection(Vector3 fallback)
        {
            return _interactionFacingDirection.sqrMagnitude > 0.001f
                ? _interactionFacingDirection.normalized
                : fallback.normalized;
        }

        private bool HasSafeDashRun(PlayerControls player, Vector3 desired)
        {
            if (Time.time < _dashSuppressedUntil
                || _pathCursor < 0
                || _pathCursor >= _worldPath.Count)
            {
                return false;
            }

            desired = Flatten(desired);
            if (desired.sqrMagnitude < 0.001f)
            {
                return false;
            }
            desired.Normalize();

            Vector3 forward = Flatten(player.transform.forward);
            if (forward.sqrMagnitude < 0.001f
                || Vector3.Dot(forward.normalized, desired) < 0.96f)
            {
                return false;
            }

            float straightDistance = Flatten(
                _worldPath[_pathCursor] - player.transform.position).magnitude;
            Vector3 previous = _worldPath[_pathCursor];
            for (int i = _pathCursor + 1; i < _worldPath.Count; i++)
            {
                Vector3 segment = Flatten(_worldPath[i] - previous);
                if (segment.sqrMagnitude < 0.001f
                    || Vector3.Dot(segment.normalized, desired) < 0.97f)
                {
                    break;
                }
                straightDistance += segment.magnitude;
                previous = _worldPath[i];
            }

            return straightDistance >= 3.0f
                && !HasBlockingCollider(player, null, desired, 1.25f);
        }

        private bool HasExtremeDashRun(PlayerControls player, Vector3 desired)
        {
            if (Time.time < _dashSuppressedUntil
                || _pathCursor < 0
                || _pathCursor >= _worldPath.Count)
            {
                return false;
            }

            desired = Flatten(desired);
            Vector3 forward = Flatten(player.transform.forward);
            if (desired.sqrMagnitude < 0.001f
                || forward.sqrMagnitude < 0.001f
                || Vector3.Dot(forward.normalized, desired.normalized) < 0.8f)
            {
                return false;
            }

            float remainingDistance = Flatten(
                _worldPath[_pathCursor] - player.transform.position).magnitude;
            for (int i = _pathCursor + 1; i < _worldPath.Count; i++)
            {
                remainingDistance += Flatten(_worldPath[i] - _worldPath[i - 1]).magnitude;
            }

            // Extreme mode intentionally ignores future corners, but it still avoids
            // dashing during the final short approach or straight into an immediate body.
            return remainingDistance >= 1.1f
                && HasGroundAhead(player, null, desired.normalized)
                && !HasBlockingCollider(player, null, desired.normalized, 1f);
        }

        private static StaticGridLocation FindRegisteredGridLocation(GameObject target)
        {
            // Only an exact registration describes this target. In particular, an
            // AttachStation's child attach point may intentionally sit one cell away
            // from the station root (for example, a lower-facing plate return).
            return target.GetComponent<StaticGridLocation>();
        }

        private void CollectDynamicBlockedCells(
            PlayerControls player,
            GridIndex start,
            float walkingSurfaceY,
            bool includeAvoidanceRadius)
        {
            _dynamicBlockedCells.Clear();
            if (_grid == null)
            {
                return;
            }

            Point3 halfSize = _grid.GetGridHalfSize();
            PlayerControls[] players = UnityEngine.Object.FindObjectsOfType<PlayerControls>();
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControls other = players[i];
                if (other == null
                    || other == player
                    || !other.enabled
                    || !other.gameObject.activeInHierarchy
                    || Mathf.Abs(other.transform.position.y - walkingSurfaceY) > 1.1f)
                {
                    continue;
                }

                Vector3 otherPosition = other.transform.position;
                GridIndex occupied = _grid.GetUnclampedGridLocationFromPos(otherPosition);
                if (!includeAvoidanceRadius || _chefAvoidanceRadius <= 0f)
                {
                    if (occupied != start && Inside(occupied, halfSize))
                    {
                        _dynamicBlockedCells.Add(occupied);
                    }
                    continue;
                }

                int cellRadius = Mathf.CeilToInt(_chefAvoidanceRadius) + 1;
                float radiusSquared = _chefAvoidanceRadius * _chefAvoidanceRadius;
                for (int x = occupied.X - cellRadius; x <= occupied.X + cellRadius; x++)
                {
                    for (int z = occupied.Z - cellRadius; z <= occupied.Z + cellRadius; z++)
                    {
                        GridIndex candidate = new GridIndex(x, start.Y, z);
                        if (candidate == start || !Inside(candidate, halfSize))
                        {
                            continue;
                        }

                        Vector3 candidatePosition = _grid.GetPosFromGridLocation(candidate);
                        if (Flatten(candidatePosition - otherPosition).sqrMagnitude <= radiusSquared)
                        {
                            _dynamicBlockedCells.Add(candidate);
                        }
                    }
                }
            }
        }

        private List<GridIndex> FindPath(GridIndex start, GridIndex goal, Point3 halfSize, float walkingSurfaceY)
        {
            Queue<GridIndex> open = new Queue<GridIndex>();
            Dictionary<GridIndex, GridIndex> parent = new Dictionary<GridIndex, GridIndex>(default(GridIndex));
            HashSet<GridIndex> visited = new HashSet<GridIndex>(default(GridIndex));
            open.Enqueue(start);
            visited.Add(start);

            int safety = 0;
            while (open.Count > 0 && safety++ < 10000)
            {
                GridIndex current = open.Dequeue();
                if (current == goal)
                {
                    List<GridIndex> result = new List<GridIndex>();
                    while (current != start)
                    {
                        result.Add(current);
                        current = parent[current];
                    }
                    result.Reverse();
                    return result;
                }

                for (int i = 0; i < FourWayOffsets.Length; i++)
                {
                    GridIndex next = current + FourWayOffsets[i];
                    if (!Inside(next, halfSize) || visited.Contains(next))
                    {
                        continue;
                    }
                    if (next != goal && !IsWalkable(next, start, walkingSurfaceY))
                    {
                        continue;
                    }
                    if (_blockedPathEdges.Contains(new GridEdge(current, next)))
                    {
                        continue;
                    }

                    visited.Add(next);
                    parent.Add(next, current);
                    open.Enqueue(next);
                }
            }

            return null;
        }

        private bool IsWalkable(GridIndex index, GridIndex start, float walkingSurfaceY)
        {
            if (index == start)
            {
                return true;
            }

            if (_dynamicBlockedCells.Contains(index))
            {
                return false;
            }

            GameObject occupant = _grid.GetGridOccupant(index);
            if (occupant != null && !IsWalkableOccupant(occupant))
            {
                return false;
            }

            RaycastHit hit;
            return TryFindGround(_grid.GetPosFromGridLocation(index), walkingSurfaceY, out hit);
        }

        private static bool IsWalkableOccupant(GameObject occupant)
        {
            return occupant.CompareTag("Travelator") || occupant.CompareTag("MovingPlatform");
        }

        private bool RejectPhysicallyBlockedPathEdge(PlayerControls player, Vector3 desired)
        {
            if (_grid == null
                || _pathCursor < 0
                || _pathCursor >= _worldPath.Count
                || (HasGroundAhead(player, null, desired)
                    && !HasStaticBlockingCollider(player, null, desired)))
            {
                return false;
            }

            GridIndex from = _grid.GetUnclampedGridLocationFromPos(player.transform.position);
            GridIndex to = _grid.GetUnclampedGridLocationFromPos(_worldPath[_pathCursor]);
            if (from == to)
            {
                return false;
            }

            _blockedPathEdges.Add(new GridEdge(from, to));
            _blockedPathEdges.Add(new GridEdge(to, from));
            _worldPath.Clear();
            _pathCursor = 0;
            _hasPath = false;
            _hasCurrentGoal = false;
            _nextRepathTime = 0f;
            _avoidDirection = Vector3.zero;
            _avoidUntil = 0f;
            _brakingPathCursor = -1;
            Status = "physical obstacle rejected current path edge";
            return true;
        }

        private void BlockCurrentRouteEdge(PlayerControls player)
        {
            if (_grid == null || _pathCursor < 0 || _pathCursor >= _worldPath.Count)
            {
                return;
            }

            GridIndex from = _grid.GetUnclampedGridLocationFromPos(player.transform.position);
            GridIndex to = _grid.GetUnclampedGridLocationFromPos(_worldPath[_pathCursor]);
            if (from == to && _pathCursor + 1 < _worldPath.Count)
            {
                to = _grid.GetUnclampedGridLocationFromPos(_worldPath[_pathCursor + 1]);
            }

            if (from != to)
            {
                _blockedPathEdges.Add(new GridEdge(from, to));
                _blockedPathEdges.Add(new GridEdge(to, from));
            }
            else if (_hasCurrentGoal)
            {
                // The blocked point is the interaction goal itself rather than an edge.
                // Try another side of the target on the next plan.
                _rejectedInteractionCells.Add(_currentGoal);
                _hasCurrentGoal = false;
            }
        }

        private static bool Inside(GridIndex index, Point3 halfSize)
        {
            return index.X >= -halfSize.X && index.X <= halfSize.X
                && index.Z >= -halfSize.Z && index.Z <= halfSize.Z;
        }

        private static bool TryFindGround(Vector3 point, float walkingSurfaceY, out RaycastHit accepted)
        {
            RaycastHit[] hits = Physics.RaycastAll(
                point + Vector3.up * 1.75f,
                Vector3.down,
                3.5f,
                -1,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestHeightDifference = float.PositiveInfinity;
            accepted = default(RaycastHit);
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                float heightDifference = Mathf.Abs(hit.point.y - walkingSurfaceY);
                if (hit.collider != null
                    && hit.normal.y > 0.45f
                    && heightDifference <= MaximumWalkableHeightDifference
                    && heightDifference < bestHeightDifference)
                {
                    accepted = hit;
                    bestHeightDifference = heightDifference;
                    found = true;
                }
            }
            return found;
        }

        private Vector3 SafeDirection(PlayerControls player, GameObject target, Vector3 desired)
        {
            if (desired.sqrMagnitude < 0.001f)
            {
                return Vector3.zero;
            }

            desired = desired.normalized;
            if (Time.time < _avoidUntil
                && _avoidDirection.sqrMagnitude > 0.001f
                && HasGroundAhead(player, target, _avoidDirection)
                && !HasBlockingCollider(player, target, _avoidDirection))
            {
                Status += "; holding avoidance direction";
                return _avoidDirection;
            }

            if (HasGroundAhead(player, target, desired)
                && !HasBlockingCollider(player, target, desired))
            {
                return desired;
            }

            Vector3 left = Quaternion.Euler(0f, -55f, 0f) * desired;
            Vector3 right = Quaternion.Euler(0f, 55f, 0f) * desired;
            bool leftClear = HasGroundAhead(player, target, left)
                && !HasBlockingCollider(player, target, left);
            bool rightClear = HasGroundAhead(player, target, right)
                && !HasBlockingCollider(player, target, right);
            Vector3 avoidance = Vector3.zero;
            if (leftClear && rightClear)
            {
                Vector3 targetDirection = target == null
                    ? desired
                    : Flatten(target.transform.position - player.transform.position).normalized;
                avoidance = Vector3.Dot(left, targetDirection) >= Vector3.Dot(right, targetDirection) ? left : right;
            }
            else if (leftClear)
            {
                avoidance = left;
            }
            else if (rightClear)
            {
                avoidance = right;
            }

            if (avoidance.sqrMagnitude > 0.001f)
            {
                _avoidDirection = avoidance.normalized;
                _avoidUntil = Time.time + 0.4f;
                Status += "; avoidance locked for 0.4s";
                return _avoidDirection;
            }

            Status += "; waiting for safe ground/dynamic obstacle";
            return Vector3.zero;
        }

        private Vector3 SafeRouteDirection(PlayerControls player, Vector3 desired)
        {
            if (desired.sqrMagnitude < 0.001f)
            {
                return Vector3.zero;
            }

            desired.Normalize();
            if (HasGroundAhead(player, null, desired)
                && !HasBlockingCollider(player, null, desired))
            {
                return desired;
            }

            // The BFS path already describes which side of a counter to use. Local
            // left/right steering here used to fight that route and could orbit a table
            // corner forever. Hold position and let the blocked-edge/stuck logic rebuild.
            Status += "; route temporarily blocked";
            return Vector3.zero;
        }

        private Vector3 FinalApproachDirection(PlayerControls player, GameObject target, Vector3 desired)
        {
            if (desired.sqrMagnitude < 0.001f)
            {
                return Vector3.zero;
            }
            desired.Normalize();

            if (_facingTarget != target)
            {
                _facingTarget = target;
                _facingBurstUntil = 0f;
            }

            // This is the same confirmed helper used by the game's own control code.
            // It rotates in place, so the local interaction scan sees the correct grid
            // direction even during a braking frame. Movement input below remains the
            // network-synchronised fallback for a non-host keyboard player.
            PlayerControlsHelper.TurnTowardsDirection(
                player.gameObject,
                desired,
                player.Movement.TurnSpeed,
                Time.deltaTime);

            Vector3 forward = Flatten(player.transform.forward);
            if (forward.sqrMagnitude > 0.001f
                && Vector3.Dot(forward.normalized, desired) >= 0.98f)
            {
                _facingBurstUntil = 0f;
                return Vector3.zero;
            }

            if (Time.time >= _facingBurstUntil && HorizontalSpeed(player) > 0.8f)
            {
                Status += "; braking while facing target";
                return Vector3.zero;
            }
            if (HasGroundAhead(player, target, desired)
                && !HasBlockingCollider(player, target, desired))
            {
                if (Time.time >= _facingBurstUntil)
                {
                    // A bounded continuous pulse turns much faster than the previous
                    // single-frame pulse/full-stop cycle, while still limiting how far
                    // the normal movement input can push into the worktop.
                    _facingBurstUntil = Time.time + 0.12f;
                }
                return desired;
            }

            // At an interaction goal, local left/right steering can undo the BFS route
            // and repeatedly drive back into an unrelated table. Stop here so the caller
            // can reject this interaction cell and request a different reachable side.
            Status += "; final approach blocked";
            return Vector3.zero;
        }

        private static bool HasGroundAhead(PlayerControls player, GameObject target, Vector3 direction)
        {
            Vector3 position = player.transform.position;
            float walkingSurfaceY = GetWalkingSurfaceY(player);
            Vector3 probe = position + direction * 0.55f + Vector3.up * 0.9f;
            RaycastHit[] hits = Physics.RaycastAll(
                probe,
                Vector3.down,
                2.1f,
                -1,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || IsPartOf(collider.transform, player.gameObject))
                {
                    continue;
                }
                if (target != null && IsRelatedTo(collider.transform, target.transform))
                {
                    continue;
                }
                if (hits[i].normal.y > 0.4f
                    && Mathf.Abs(hits[i].point.y - walkingSurfaceY) <= MaximumWalkableHeightDifference)
                {
                    return true;
                }
            }
            return false;
        }

        private static Vector3 CalculateRepulsion(
            Vector3 playerPosition,
            IList<Vector3> threatPositions,
            Vector3 fallback)
        {
            Vector3 result = Vector3.zero;
            for (int i = 0; i < threatPositions.Count; i++)
            {
                Vector3 away = Flatten(playerPosition - threatPositions[i]);
                float sqrDistance = away.sqrMagnitude;
                if (sqrDistance > 0.0001f)
                {
                    result += away.normalized / Mathf.Max(0.25f, Mathf.Sqrt(sqrDistance));
                }
            }

            result = Flatten(result);
            if (result.sqrMagnitude < 0.001f)
            {
                result = Flatten(fallback);
            }
            return result.sqrMagnitude > 0.001f ? result.normalized : Vector3.forward;
        }

        private static float MinimumHorizontalSqrDistance(Vector3 point, IList<Vector3> positions)
        {
            float minimum = float.PositiveInfinity;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 delta = Flatten(point - positions[i]);
                if (delta.sqrMagnitude < minimum)
                {
                    minimum = delta.sqrMagnitude;
                }
            }
            return minimum;
        }

        private static bool HasBlockingCollider(PlayerControls player, GameObject target, Vector3 direction)
        {
            return HasBlockingCollider(player, target, direction, 0.55f);
        }

        private static bool HasOtherChefAhead(
            PlayerControls player,
            Vector3 direction,
            float distance)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                player.transform.position + Vector3.up * 0.45f,
                0.24f,
                direction,
                distance,
                -1,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || IsPartOf(collider.transform, player.gameObject))
                {
                    continue;
                }
                PlayerControls other = collider.GetComponentInParent<PlayerControls>();
                if (other != null && other != player)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasBlockingCollider(
            PlayerControls player,
            GameObject target,
            Vector3 direction,
            float distance)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                player.transform.position + Vector3.up * 0.45f,
                0.2f,
                direction,
                distance,
                -1,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                {
                    continue;
                }
                Transform hitTransform = collider.transform;
                if (IsPartOf(hitTransform, player.gameObject))
                {
                    continue;
                }
                if (target != null && IsRelatedTo(hitTransform, target.transform))
                {
                    continue;
                }

                ClientPlayerAttachmentCarrier carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
                GameObject carried = carrier == null ? null : carrier.InspectCarriedItem();
                if (carried != null && IsPartOf(hitTransform, carried))
                {
                    continue;
                }

                PlayerControls otherPlayer = collider.GetComponentInParent<PlayerControls>();
                if (otherPlayer != null && otherPlayer != player)
                {
                    return true;
                }

                Rigidbody body = collider.attachedRigidbody;
                if (body != null && !body.isKinematic && body.gameObject != player.gameObject)
                {
                    return true;
                }


                GroundCast groundCast = player.GetComponent<GroundCast>();
                if (groundCast != null && collider == groundCast.GetGroundCollider())
                {
                    continue;
                }
                if (collider.CompareTag("Travelator") || collider.CompareTag("MovingPlatform"))
                {
                    continue;
                }

                // The cast runs through the chef's body rather than along the floor, so
                // any remaining static/kinematic collider is a wall, counter, or prop.
                return true;
            }
            return false;
        }

        private static bool HasStaticBlockingCollider(PlayerControls player, GameObject target, Vector3 direction)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                player.transform.position + Vector3.up * 0.45f,
                0.2f,
                direction,
                0.55f,
                -1,
                QueryTriggerInteraction.Ignore);
            GroundCast groundCast = player.GetComponent<GroundCast>();
            ClientPlayerAttachmentCarrier carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
            GameObject carried = carrier == null ? null : carrier.InspectCarriedItem();

            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || IsPartOf(collider.transform, player.gameObject))
                {
                    continue;
                }
                if (target != null && IsRelatedTo(collider.transform, target.transform))
                {
                    continue;
                }
                if (carried != null && IsPartOf(collider.transform, carried))
                {
                    continue;
                }
                if (collider.GetComponentInParent<PlayerControls>() != null)
                {
                    continue;
                }
                Rigidbody body = collider.attachedRigidbody;
                if (body != null && !body.isKinematic)
                {
                    continue;
                }
                if (groundCast != null && collider == groundCast.GetGroundCollider())
                {
                    continue;
                }
                if (collider.CompareTag("Travelator") || collider.CompareTag("MovingPlatform"))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static float GetWalkingSurfaceY(PlayerControls player)
        {
            GroundCast groundCast = player == null ? null : player.GetComponent<GroundCast>();
            if (groundCast != null && groundCast.HasGroundContact())
            {
                return groundCast.GetGroundPoint().y;
            }
            return player == null ? 0f : player.transform.position.y;
        }

        private static bool IsPartOf(Transform candidate, GameObject root)
        {
            return candidate == root.transform || candidate.IsChildOf(root.transform);
        }

        private static bool IsRelatedTo(Transform left, Transform right)
        {
            return left == right || left.IsChildOf(right) || right.IsChildOf(left);
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private struct GridEdge : IEquatable<GridEdge>
        {
            internal readonly GridIndex From;
            internal readonly GridIndex To;

            internal GridEdge(GridIndex from, GridIndex to)
            {
                From = from;
                To = to;
            }

            public bool Equals(GridEdge other)
            {
                return From == other.From && To == other.To;
            }

            public override bool Equals(object obj)
            {
                return obj is GridEdge && Equals((GridEdge)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (From.GetHashCode() * 397) ^ To.GetHashCode();
                }
            }
        }
    }
}
