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

        internal string Status { get; private set; }

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
            _avoidDirection = Vector3.zero;
            _avoidUntil = 0f;
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

            Vector3 playerPosition = player.transform.position;
            Vector3 targetPosition = target.transform.position;
            Vector3 direct = Flatten(targetPosition - playerPosition);

            if (!searchAllNeighbourCells && direct.sqrMagnitude < 0.65f * 0.65f)
            {
                atInteractionCell = true;
                Status = "inside interaction distance";
                return direct.sqrMagnitude > 0.01f ? direct.normalized : player.transform.forward;
            }

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
            if (targetChanged)
            {
                _rejectedInteractionCells.Clear();
                _hasCurrentGoal = false;
                _searchAllNeighbourCells = searchAllNeighbourCells;
            }
            bool stuck = Time.time - _stuckSince > 1.25f;
            if (targetChanged || Time.time >= _nextRepathTime || stuck)
            {
                _target = target;
                BuildPath(player, target, searchAllNeighbourCells);
                _nextRepathTime = Time.time + (stuck ? 0.35f : 0.9f);
                if (stuck)
                {
                    _stuckSince = Time.time;
                }
            }

            while (_pathCursor < _worldPath.Count
                && Flatten(_worldPath[_pathCursor] - playerPosition).sqrMagnitude < 0.38f * 0.38f)
            {
                _pathCursor++;
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
                return SafeDirection(player, target, direct.normalized);
            }

            Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
            if (_pathCursor == _worldPath.Count - 1 && toWaypoint.sqrMagnitude < 0.35f * 0.35f)
            {
                atInteractionCell = true;
                Status = "approaching target from final cell";
                return SafeDirection(player, target, direct.normalized);
            }

            Status = "following waypoint " + (_pathCursor + 1) + "/" + _worldPath.Count;
            return SafeDirection(player, target, toWaypoint.normalized);
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
                hasEscapePath = true;
                Vector3 toWaypoint = Flatten(_worldPath[_pathCursor] - playerPosition);
                Status = "avoiding player via waypoint " + (_pathCursor + 1) + "/" + _worldPath.Count;
                return SafeDirection(player, null, toWaypoint.normalized);
            }

            Vector3 fallback = CalculateRepulsion(playerPosition, threatPositions, player.transform.forward);
            if (!_hasPath && fallback.sqrMagnitude > 0.001f)
            {
                Status = "no grid escape path; using safe repulsion";
                return SafeDirection(player, null, fallback.normalized);
            }

            hasEscapePath = _hasPath;
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
            Status = "interaction cell rejected";
            return true;
        }

        private void BuildPath(PlayerControls player, GameObject target, bool searchAllNeighbourCells)
        {
            _worldPath.Clear();
            _pathCursor = 0;
            _hasPath = false;
            _hasCurrentGoal = false;
            _avoidanceMode = false;

            GridManager playerGrid = GameUtils.GetGridManager(player.transform);
            StaticGridLocation registeredTargetLocation = FindRegisteredGridLocation(target);
            GridManager targetGrid = registeredTargetLocation == null
                ? GameUtils.GetGridManager(target.transform)
                : registeredTargetLocation.AccessGridManager;
            if (playerGrid == null || targetGrid == null || playerGrid != targetGrid)
            {
                _grid = null;
                Status = "player and target are not on the same grid";
                return;
            }

            _grid = playerGrid;
            GridIndex start = _grid.GetUnclampedGridLocationFromPos(player.transform.position);
            GridIndex rawTargetIndex = registeredTargetLocation == null
                ? _grid.GetUnclampedGridLocationFromPos(target.transform.position)
                : registeredTargetLocation.GridIndex;

            // A stack on a worktop is often one grid level above the chef. This search only
            // expands X/Z, so its goal must stay on the chef's current walking layer.
            GridIndex targetIndex = new GridIndex(rawTargetIndex.X, start.Y, rawTargetIndex.Z);
            Point3 halfSize = _grid.GetGridHalfSize();

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
                            && IsWalkable(goal, start, player.transform.position.y))
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
                    if (Inside(goal, halfSize) && IsWalkable(goal, start, player.transform.position.y))
                    {
                        goals.Add(goal);
                    }
                }
            }

            if (goals.Count == 0)
            {
                Status = "target has no walkable adjacent cell on player layer Y=" + start.Y;
                return;
            }

            List<GridIndex> best = null;
            GridIndex bestGoal = default(GridIndex);
            for (int i = 0; i < goals.Count; i++)
            {
                List<GridIndex> path = FindPath(start, goals[i], halfSize, player.transform.position.y);
                if (path != null && (best == null || path.Count < best.Count))
                {
                    best = path;
                    bestGoal = goals[i];
                }
            }

            if (best == null)
            {
                Status = "path search failed on player layer Y=" + start.Y;
                return;
            }

            _hasPath = true;
            _currentGoal = bestGoal;
            _hasCurrentGoal = true;
            Status = "path built with " + best.Count + " waypoint(s) on player layer Y=" + start.Y;

            for (int i = 0; i < best.Count; i++)
            {
                Vector3 point = _grid.GetPosFromGridLocation(best[i]);
                RaycastHit hit;
                if (TryFindGround(point, player.transform.position.y, out hit))
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

            _grid = GameUtils.GetGridManager(player.transform);
            if (_grid == null)
            {
                Status = "avoidance has no player grid";
                return;
            }

            Vector3 playerPosition = player.transform.position;
            GridIndex start = _grid.GetUnclampedGridLocationFromPos(playerPosition);
            Point3 halfSize = _grid.GetGridHalfSize();
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
                        || !IsWalkable(next, start, playerPosition.y))
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
                if (TryFindGround(point, playerPosition.y, out hit))
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

        private static StaticGridLocation FindRegisteredGridLocation(GameObject target)
        {
            // Only an exact registration describes this target. In particular, an
            // AttachStation's child attach point may intentionally sit one cell away
            // from the station root (for example, a lower-facing plate return).
            return target.GetComponent<StaticGridLocation>();
        }

        private List<GridIndex> FindPath(GridIndex start, GridIndex goal, Point3 halfSize, float playerY)
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
                    if (next != goal && !IsWalkable(next, start, playerY))
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

        private bool IsWalkable(GridIndex index, GridIndex start, float playerY)
        {
            if (index == start)
            {
                return true;
            }

            GameObject occupant = _grid.GetGridOccupant(index);
            if (occupant != null && !IsWalkableOccupant(occupant))
            {
                return false;
            }

            RaycastHit hit;
            return TryFindGround(_grid.GetPosFromGridLocation(index), playerY, out hit);
        }

        private static bool IsWalkableOccupant(GameObject occupant)
        {
            return occupant.CompareTag("Travelator") || occupant.CompareTag("MovingPlatform");
        }

        private static bool Inside(GridIndex index, Point3 halfSize)
        {
            return index.X >= -halfSize.X && index.X <= halfSize.X
                && index.Z >= -halfSize.Z && index.Z <= halfSize.Z;
        }

        private static bool TryFindGround(Vector3 point, float playerY, out RaycastHit accepted)
        {
            RaycastHit[] hits = Physics.RaycastAll(
                point + Vector3.up * 1.75f,
                Vector3.down,
                3.5f,
                -1,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider != null && hit.normal.y > 0.45f && Mathf.Abs(hit.point.y - playerY) < 1.1f)
                {
                    accepted = hit;
                    return true;
                }
            }

            accepted = default(RaycastHit);
            return false;
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
                && !HasDynamicBlockingCollider(player, target, _avoidDirection))
            {
                Status += "; holding avoidance direction";
                return _avoidDirection;
            }

            if (HasGroundAhead(player, target, desired)
                && !HasDynamicBlockingCollider(player, target, desired))
            {
                return desired;
            }

            Vector3 left = Quaternion.Euler(0f, -55f, 0f) * desired;
            Vector3 right = Quaternion.Euler(0f, 55f, 0f) * desired;
            bool leftClear = HasGroundAhead(player, target, left)
                && !HasDynamicBlockingCollider(player, target, left);
            bool rightClear = HasGroundAhead(player, target, right)
                && !HasDynamicBlockingCollider(player, target, right);
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

        private static bool HasGroundAhead(PlayerControls player, GameObject target, Vector3 direction)
        {
            Vector3 position = player.transform.position;
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
                if (target != null && IsPartOf(collider.transform, target))
                {
                    continue;
                }
                if (hits[i].normal.y > 0.4f && Mathf.Abs(hits[i].point.y - position.y) < 1.2f)
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

        private static bool HasDynamicBlockingCollider(PlayerControls player, GameObject target, Vector3 direction)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                player.transform.position + Vector3.up * 0.45f,
                0.2f,
                direction,
                0.55f,
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
                if (target != null && IsPartOf(hitTransform, target))
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
            }
            return false;
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
