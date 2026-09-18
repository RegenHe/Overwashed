using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overcooked2DishwasherBot
{
    internal sealed class GridPathNavigator
    {
        private static readonly GridIndex[] FourWayOffsets =
        {
            new GridIndex(1, 0, 0),
            new GridIndex(-1, 0, 0),
            new GridIndex(0, 0, 1),
            new GridIndex(0, 0, -1)
        };

        private readonly List<Vector3> _worldPath = new List<Vector3>();
        private readonly HashSet<GridIndex> _rejectedInteractionCells = new HashSet<GridIndex>(default(GridIndex));
        private GameObject _target;
        private GridNavSpace _navSpace;
        private GridManager _navGrid;
        private GridIndex _currentGoal;
        private bool _hasCurrentGoal;
        private bool _searchAllNeighbourCells;
        private bool _routeFound;
        private bool _avoidanceMode;
        private int _pathCursor;
        private float _nextRepathTime;
        private float _lastWaypointDistance;
        private float _lastProgressTime;
        private float _facingStartedTime;

        internal string Status { get; private set; }

        internal void Clear()
        {
            _worldPath.Clear();
            _rejectedInteractionCells.Clear();
            _target = null;
            _navSpace = null;
            _navGrid = null;
            _hasCurrentGoal = false;
            _searchAllNeighbourCells = false;
            _routeFound = false;
            _avoidanceMode = false;
            _pathCursor = 0;
            _nextRepathTime = 0f;
            _facingStartedTime = 0f;
            ResetProgressTracking();
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
            if (player == null || target == null)
            {
                return Vector3.zero;
            }

            bool targetChanged = _avoidanceMode
                || _target != target
                || _searchAllNeighbourCells != searchAllNeighbourCells;
            if (targetChanged)
            {
                _target = target;
                _avoidanceMode = false;
                _searchAllNeighbourCells = searchAllNeighbourCells;
                _rejectedInteractionCells.Clear();
                ClearRoute();
                _facingStartedTime = 0f;
                _nextRepathTime = 0f;
            }

            if (Time.time >= _nextRepathTime)
            {
                BuildTargetRoute(player, target, searchAllNeighbourCells);
                _nextRepathTime = Time.time + (_routeFound ? 1f : 0.4f);
            }

            Vector3 playerPosition = player.transform.position;
            AdvancePastReachedWaypoints(playerPosition);
            if (_pathCursor < _worldPath.Count)
            {
                Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
                if (TrackProgressAndNeedsRepath(toWaypoint.magnitude))
                {
                    BuildTargetRoute(player, target, searchAllNeighbourCells);
                    _nextRepathTime = Time.time + 0.4f;
                    AdvancePastReachedWaypoints(playerPosition);
                    if (_pathCursor >= _worldPath.Count)
                    {
                        return Vector3.zero;
                    }
                    toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
                }

                Status = "following game route " + (_pathCursor + 1) + "/" + _worldPath.Count;
                return SafeMovementDirection(player, toWaypoint.normalized);
            }

            if (!_routeFound)
            {
                Status = "game navigation found no route";
                return Vector3.zero;
            }

            if (_hasCurrentGoal
                && _navGrid != null
                && _navGrid.GetGridLocationFromPos(playerPosition) != _currentGoal)
            {
                _facingStartedTime = 0f;
                BuildTargetRoute(player, target, searchAllNeighbourCells);
                _nextRepathTime = Time.time + 0.4f;
                AdvancePastReachedWaypoints(playerPosition);
                if (_pathCursor < _worldPath.Count)
                {
                    Vector3 backToGoal = Flatten(_worldPath[_pathCursor] - playerPosition);
                    Status = "returning to interaction cell after facing movement";
                    return SafeMovementDirection(player, backToGoal.normalized);
                }
                if (!_routeFound)
                {
                    return Vector3.zero;
                }
            }

            atInteractionCell = true;
            Vector3 direct = Flatten(target.transform.position - playerPosition);
            return FaceInteractionTarget(player, direct);
        }

        internal Vector3 DirectionAwayFrom(
            PlayerControls player,
            IList<Vector3> threatPositions,
            float safeDistance,
            out bool hasEscapePath)
        {
            hasEscapePath = false;
            if (player == null || threatPositions == null || threatPositions.Count == 0)
            {
                return Vector3.zero;
            }

            if (!_avoidanceMode)
            {
                _avoidanceMode = true;
                _target = null;
                _rejectedInteractionCells.Clear();
                ClearRoute();
                _facingStartedTime = 0f;
                _nextRepathTime = 0f;
            }

            if (Time.time >= _nextRepathTime)
            {
                BuildAvoidanceRoute(player, threatPositions, safeDistance);
                _nextRepathTime = Time.time + (_routeFound ? 0.35f : 0.2f);
            }

            Vector3 playerPosition = player.transform.position;
            AdvancePastReachedWaypoints(playerPosition);
            if (_pathCursor >= _worldPath.Count)
            {
                hasEscapePath = _routeFound;
                Status = _routeFound ? "at avoidance goal" : "game navigation found no escape route";
                return Vector3.zero;
            }

            hasEscapePath = true;
            Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
            Status = "avoiding player via game route " + (_pathCursor + 1) + "/" + _worldPath.Count;
            return SafeMovementDirection(player, toWaypoint.normalized);
        }

        internal bool RejectCurrentInteractionCell()
        {
            if (!_hasCurrentGoal)
            {
                return false;
            }

            _rejectedInteractionCells.Add(_currentGoal);
            ClearRoute();
            _facingStartedTime = 0f;
            _nextRepathTime = 0f;
            Status = "interaction cell rejected";
            return true;
        }

        private void BuildTargetRoute(PlayerControls player, GameObject target, bool searchAllNeighbourCells)
        {
            ClearRoute();
            if (!EnsureGameNavigation())
            {
                Status = "game navigation is unavailable";
                return;
            }

            GridIndex start = _navGrid.GetGridLocationFromPos(player.transform.position);
            GridIndex targetIndex = _navGrid.GetGridLocationFromPos(target.transform.position);
            Point3 halfSize = _navGrid.GetGridHalfSize();
            Point2 startPoint = _navSpace.GetNavPoint(player.transform.position);

            List<Vector3> bestPath = null;
            GridIndex bestGoal = default(GridIndex);
            float bestTieDistance = float.PositiveInfinity;

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
                        ConsiderTargetGoal(
                            start,
                            startPoint,
                            targetIndex + new GridIndex(x, 0, z),
                            halfSize,
                            player.transform.position,
                            ref bestPath,
                            ref bestGoal,
                            ref bestTieDistance);
                    }
                }
            }
            else
            {
                for (int i = 0; i < FourWayOffsets.Length; i++)
                {
                    ConsiderTargetGoal(
                        start,
                        startPoint,
                        targetIndex + FourWayOffsets[i],
                        halfSize,
                        player.transform.position,
                        ref bestPath,
                        ref bestGoal,
                        ref bestTieDistance);
                }
            }

            if (bestPath == null)
            {
                Status = "no reachable interaction cell in game navigation";
                return;
            }

            _worldPath.AddRange(bestPath);
            _routeFound = true;
            _currentGoal = bestGoal;
            _hasCurrentGoal = true;
            ResetProgressTracking();
            Status = "game route built with " + bestPath.Count + " waypoint(s)";
        }

        private void ConsiderTargetGoal(
            GridIndex start,
            Point2 startPoint,
            GridIndex goal,
            Point3 halfSize,
            Vector3 playerPosition,
            ref List<Vector3> bestPath,
            ref GridIndex bestGoal,
            ref float bestTieDistance)
        {
            if (!Inside(goal, halfSize)
                || _rejectedInteractionCells.Contains(goal)
                || !IsNavigationCellOpen(goal, start))
            {
                return;
            }

            Vector3 goalPosition = _navGrid.GetPosFromGridLocation(goal);
            List<Vector3> candidate = FindGameRoute(start, startPoint, goal, goalPosition);
            if (candidate == null)
            {
                return;
            }

            float tieDistance = Flatten(goalPosition - playerPosition).sqrMagnitude;
            if (bestPath == null
                || candidate.Count < bestPath.Count
                || (candidate.Count == bestPath.Count && tieDistance < bestTieDistance))
            {
                bestPath = candidate;
                bestGoal = goal;
                bestTieDistance = tieDistance;
            }
        }

        private List<Vector3> FindGameRoute(
            GridIndex start,
            Point2 startPoint,
            GridIndex goal,
            Vector3 goalPosition)
        {
            if (start == goal)
            {
                return new List<Vector3>();
            }

            try
            {
                List<Vector3> path = _navSpace.FindPath(startPoint, _navSpace.GetNavPoint(goalPosition));
                return path != null && path.Count > 0 ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private void BuildAvoidanceRoute(
            PlayerControls player,
            IList<Vector3> threatPositions,
            float safeDistance)
        {
            ClearRoute();
            if (!EnsureGameNavigation())
            {
                Status = "game navigation is unavailable for avoidance";
                return;
            }

            Vector3 playerPosition = player.transform.position;
            GridIndex start = _navGrid.GetGridLocationFromPos(playerPosition);
            Point2 startPoint = _navSpace.GetNavPoint(playerPosition);
            Point3 halfSize = _navGrid.GetGridHalfSize();
            int maxDepth = Mathf.Clamp(Mathf.CeilToInt(safeDistance) + 2, 3, 6);
            float safeDistanceSquared = safeDistance * safeDistance;
            Vector3 preferred = CalculateRepulsion(playerPosition, threatPositions, player.transform.forward);

            List<Vector3> bestSafePath = null;
            float bestSafeScore = float.NegativeInfinity;
            List<Vector3> bestFallbackPath = null;
            float bestFallbackScore = float.NegativeInfinity;

            for (int x = -maxDepth; x <= maxDepth; x++)
            {
                for (int z = -maxDepth; z <= maxDepth; z++)
                {
                    int manhattan = Mathf.Abs(x) + Mathf.Abs(z);
                    if (manhattan == 0 || manhattan > maxDepth)
                    {
                        continue;
                    }

                    GridIndex goal = start + new GridIndex(x, 0, z);
                    if (!Inside(goal, halfSize) || !IsNavigationCellOpen(goal, start))
                    {
                        continue;
                    }

                    Vector3 goalPosition = _navGrid.GetPosFromGridLocation(goal);
                    List<Vector3> path = FindGameRoute(start, startPoint, goal, goalPosition);
                    if (path == null)
                    {
                        continue;
                    }

                    float minimumDistanceSquared = MinimumHorizontalSqrDistance(goalPosition, threatPositions);
                    Vector3 fromPlayer = Flatten(goalPosition - playerPosition);
                    float alignment = fromPlayer.sqrMagnitude > 0.001f
                        ? Vector3.Dot(fromPlayer.normalized, preferred)
                        : -1f;
                    if (minimumDistanceSquared >= safeDistanceSquared)
                    {
                        float score = minimumDistanceSquared + alignment * 0.35f - path.Count * 0.08f;
                        if (bestSafePath == null
                            || path.Count < bestSafePath.Count
                            || (path.Count == bestSafePath.Count && score > bestSafeScore))
                        {
                            bestSafePath = path;
                            bestSafeScore = score;
                        }
                    }
                    else
                    {
                        float score = minimumDistanceSquared + alignment * 0.25f - path.Count * 0.08f;
                        if (bestFallbackPath == null || score > bestFallbackScore)
                        {
                            bestFallbackPath = path;
                            bestFallbackScore = score;
                        }
                    }
                }
            }

            List<Vector3> selected = bestSafePath ?? bestFallbackPath;
            if (selected == null)
            {
                Status = "game navigation found no avoidance route";
                return;
            }

            _worldPath.AddRange(selected);
            _routeFound = true;
            ResetProgressTracking();
            Status = bestSafePath != null
                ? "game route built to a safe avoidance cell"
                : "game route built toward the safest reachable cell";
        }

        private bool EnsureGameNavigation()
        {
            try
            {
                GridNavSpace navSpace = GameUtils.GetGridNavSpace();
                GridManager navGrid = navSpace == null ? null : GameUtils.GetGridManager(navSpace.transform);
                if (navSpace == null || navGrid == null)
                {
                    return false;
                }

                if (_navSpace != navSpace || _navGrid != navGrid)
                {
                    _navSpace = navSpace;
                    _navGrid = navGrid;
                    _worldPath.Clear();
                    _pathCursor = 0;
                }
                return true;
            }
            catch
            {
                _navSpace = null;
                _navGrid = null;
                return false;
            }
        }

        private bool IsNavigationCellOpen(GridIndex index, GridIndex start)
        {
            if (index == start)
            {
                return true;
            }
            GameObject occupant = _navGrid.GetGridOccupant(index);
            return occupant == null || IsWalkableOccupant(occupant);
        }

        private static bool IsWalkableOccupant(GameObject occupant)
        {
            return occupant.CompareTag("Travelator") || occupant.CompareTag("MovingPlatform");
        }

        private void AdvancePastReachedWaypoints(Vector3 playerPosition)
        {
            while (_pathCursor < _worldPath.Count
                && Flatten(_worldPath[_pathCursor] - playerPosition).sqrMagnitude < 0.3f * 0.3f)
            {
                _pathCursor++;
                ResetProgressTracking();
            }
        }

        private bool TrackProgressAndNeedsRepath(float distance)
        {
            if (float.IsPositiveInfinity(_lastWaypointDistance)
                || distance < _lastWaypointDistance - 0.025f)
            {
                _lastWaypointDistance = distance;
                _lastProgressTime = Time.time;
                return false;
            }

            if (distance < _lastWaypointDistance)
            {
                _lastWaypointDistance = distance;
            }
            return Time.time - _lastProgressTime >= 1.1f;
        }

        private void ResetProgressTracking()
        {
            _lastWaypointDistance = float.PositiveInfinity;
            _lastProgressTime = Time.time;
        }

        private void ClearRoute()
        {
            _worldPath.Clear();
            _pathCursor = 0;
            _routeFound = false;
            _hasCurrentGoal = false;
            ResetProgressTracking();
        }

        private Vector3 FaceInteractionTarget(PlayerControls player, Vector3 direct)
        {
            Vector3 desired = direct.sqrMagnitude > 0.01f
                ? direct.normalized
                : Flatten(player.transform.forward).normalized;
            Vector3 forward = Flatten(player.transform.forward).normalized;
            if (desired.sqrMagnitude < 0.001f || Vector3.Dot(forward, desired) >= 0.94f)
            {
                Status = "at interaction cell and facing target";
                _facingStartedTime = 0f;
                return Vector3.zero;
            }

            if (_facingStartedTime <= 0f)
            {
                _facingStartedTime = Time.time;
            }
            if (Time.time - _facingStartedTime <= 0.22f)
            {
                // The game's movement implementation rotates from the normal input axis.
                // A short bounded input burst synchronises facing on host and clients while
                // preventing the old unlimited straight-line push into a counter.
                Status = "turning toward interaction target";
                return desired;
            }

            Status = "interaction facing burst complete";
            return Vector3.zero;
        }

        private static Vector3 SafeMovementDirection(PlayerControls player, Vector3 desired)
        {
            desired = Flatten(desired);
            if (desired.sqrMagnitude < 0.001f)
            {
                return Vector3.zero;
            }
            desired.Normalize();
            return HasOtherChefAhead(player, desired) ? Vector3.zero : desired;
        }

        private static bool HasOtherChefAhead(PlayerControls player, Vector3 direction)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                player.transform.position + Vector3.up * 0.45f,
                0.17f,
                direction,
                0.42f,
                -1,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || IsPartOf(collider.transform, player.gameObject))
                {
                    continue;
                }
                PlayerControls otherPlayer = collider.GetComponentInParent<PlayerControls>();
                if (otherPlayer != null && otherPlayer != player)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Inside(GridIndex index, Point3 halfSize)
        {
            return index.X >= -halfSize.X && index.X <= halfSize.X
                && index.Z >= -halfSize.Z && index.Z <= halfSize.Z;
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
                float distance = Flatten(point - positions[i]).sqrMagnitude;
                if (distance < minimum)
                {
                    minimum = distance;
                }
            }
            return minimum;
        }

        private static bool IsPartOf(Transform candidate, GameObject root)
        {
            return candidate == root.transform || candidate.IsChildOf(root.transform);
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }
    }
}
