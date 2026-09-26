using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class TeleportEnemyAI : MonoBehaviour
{
    public enum State
    {
        Idle,
        Patrol,
        Chase,
        Attack,
        Search,
        Teleport
    }

    [Header("State")]
    [SerializeField] private State currentState = State.Patrol;

    [Header("References")]
    [SerializeField] private Transform playerTransform;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3.5f;

    [Header("Patrol")]
    [SerializeField] private float patrolRadius = 8f;
    [SerializeField] private float waitAtPatrolPoint = 1.5f;

    [Header("Detection")]
    [SerializeField] private float scanRadius = 5f;
    [SerializeField] private float scanInterval = 0.7f;
    [SerializeField] private LayerMask obstacleLayer;

    [Header("Attack")]
    [SerializeField] private float attackRange = 1.8f;
    [SerializeField] private float attackCooldown = 1.5f;
    [SerializeField] private int attackDamage = 15;

    [Header("Search")]
    [SerializeField] private float searchDuration = 3f;

    [Header("Teleport")]
    [SerializeField] private float teleportCooldown = 3.5f;
    [SerializeField] private float maxTeleportDistance = 6f;
    [SerializeField] private float minTeleportDistance = 2.5f;
    [SerializeField] private float teleportTriggerDistance = 4.5f;

    private const float ObstacleCheckRadius = 0.35f;

    private NavMeshAgent agent;
    private Renderer enemyRenderer;
    private Color baseRendererColor;
    private Coroutine colorEffectRoutine;
    private NavMeshPath teleportPath;

    private Vector3 lastKnownPos;
    private Vector3 currentPatrolPoint;

    private float stateTimer;
    private float lastAttackTime = -999f;
    private float lastScanTime = -999f;
    private float lastTeleportTime = -999f;
    private bool lastScanSawPlayer;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        enemyRenderer = GetComponentInChildren<Renderer>();
        teleportPath = new NavMeshPath();
        if (enemyRenderer != null)
            baseRendererColor = enemyRenderer.material.color;

        if (agent == null)
        {
            Debug.LogError("TeleportEnemyAI requires a NavMeshAgent component.");
            enabled = false;
            return;
        }

        agent.speed = moveSpeed;

        if (obstacleLayer.value == 0)
        {
            int wallsLayer = LayerMask.NameToLayer("walls");
            if (wallsLayer >= 0)
                obstacleLayer = 1 << wallsLayer;
        }

        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
                playerTransform = player.transform;
        }

        currentPatrolPoint = transform.position;
    }

    private void Update()
    {
        switch (currentState)
        {
            case State.Idle:
                IdleUpdate();
                break;
            case State.Patrol:
                PatrolUpdate();
                break;
            case State.Chase:
                ChaseUpdate();
                break;
            case State.Attack:
                AttackUpdate();
                break;
            case State.Search:
                SearchUpdate();
                break;
            case State.Teleport:
                TeleportUpdate();
                break;
        }
    }

    private void ChangeState(State newState)
    {
        if (currentState == newState)
        {
            if (newState == State.Search)
                stateTimer = searchDuration;

            if (newState == State.Idle)
                stateTimer = waitAtPatrolPoint;

            return;
        }

        currentState = newState;

        if (newState == State.Search)
            stateTimer = searchDuration;

        if (newState == State.Idle)
            stateTimer = waitAtPatrolPoint;

        if (newState == State.Patrol)
            agent.isStopped = false;
    }

    private void IdleUpdate()
    {
        agent.isStopped = true;

        if (TryRespondToPlayer())
        {
            return;
        }

        stateTimer -= Time.deltaTime;
        if (stateTimer <= 0f)
            ChangeState(State.Patrol);
    }

    private void PatrolUpdate()
    {
        agent.isStopped = false;

        if (TryRespondToPlayer())
        {
            return;
        }

        if (!agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.2f)
        {
            if (SetRandomPatrolPoint())
                currentPatrolPoint = agent.destination;
        }

        if (agent.pathPending)
            return;

        if (agent.remainingDistance <= agent.stoppingDistance + 0.2f && agent.velocity.sqrMagnitude < 0.05f)
            ChangeState(State.Idle);
    }

    private void ChaseUpdate()
    {
        agent.isStopped = false;

        if (playerTransform == null)
        {
            ChangeState(State.Patrol);
            return;
        }

        if (!TryScanForPlayer())
        {
            lastKnownPos = playerTransform.position;
            ChangeState(State.Search);
            return;
        }

        lastKnownPos = playerTransform.position;
        float distance = FlatDistance(transform.position, playerTransform.position);

        if (distance <= attackRange)
        {
            ChangeState(State.Attack);
            return;
        }

        if (ShouldTeleport(distance))
        {
            ChangeState(State.Teleport);
            return;
        }

        agent.SetDestination(playerTransform.position);
    }

    private void AttackUpdate()
    {
        agent.isStopped = true;

        if (playerTransform == null)
        {
            ChangeState(State.Patrol);
            return;
        }

        float distance = FlatDistance(transform.position, playerTransform.position);

        if (distance > attackRange)
        {
            ChangeState(State.Chase);
            return;
        }

        if (!HasLineOfSightToPlayer())
        {
            lastKnownPos = playerTransform.position;
            ChangeState(State.Search);
            return;
        }

        LookAt(playerTransform.position);

        if (Time.time >= lastAttackTime + attackCooldown)
        {
            DoAttack();
            lastAttackTime = Time.time;
        }
    }

    private void SearchUpdate()
    {
        agent.isStopped = false;

        if (TryRespondToPlayer())
        {
            return;
        }

        agent.SetDestination(lastKnownPos);

        bool reachedLastKnown = FlatDistance(transform.position, lastKnownPos) <= 0.3f;
        bool badPath = !agent.pathPending &&
                       (agent.pathStatus == NavMeshPathStatus.PathInvalid ||
                        agent.pathStatus == NavMeshPathStatus.PathPartial);

        if (reachedLastKnown || badPath)
        {
            agent.isStopped = true;
            stateTimer -= Time.deltaTime;
            transform.Rotate(0f, 120f * Time.deltaTime, 0f);

            if (stateTimer <= 0f)
                ChangeState(State.Patrol);
        }
    }

    private void TeleportUpdate()
    {
        agent.isStopped = true;

        if (playerTransform == null)
        {
            ChangeState(State.Patrol);
            return;
        }

        if (Time.time < lastTeleportTime + teleportCooldown)
        {
            ChangeState(State.Chase);
            return;
        }

        if (TryGetTeleportDestination(out Vector3 destination))
        {
            Vector3 oldPosition = transform.position;
            agent.Warp(destination);
            lastTeleportTime = Time.time;

            PlayTemporaryColor(Color.cyan);

            Debug.Log($"TeleportEnemyAI: teleported from {oldPosition} to {destination}.");
        }
        else
        {
            Debug.LogWarning("TeleportEnemyAI: teleport failed, no valid destination found.");
        }

        ChangeState(State.Chase);
    }

    private bool TryScanForPlayer()
    {
        if (playerTransform == null)
            return false;

        if (Time.time < lastScanTime + scanInterval)
            return lastScanSawPlayer;

        lastScanTime = Time.time;

        float distance = FlatDistance(transform.position, playerTransform.position);
        if (distance > scanRadius)
        {
            lastScanSawPlayer = false;
            return false;
        }

        if (!HasLineOfSightToPlayer())
        {
            lastScanSawPlayer = false;
            return false;
        }

        lastKnownPos = playerTransform.position;
        lastScanSawPlayer = true;
        return true;
    }

    private bool TryRespondToPlayer()
    {
        if (!TryScanForPlayer())
            return false;

        float distance = FlatDistance(transform.position, playerTransform.position);

        if (distance <= attackRange)
        {
            ChangeState(State.Attack);
            return true;
        }

        if (ShouldTeleport(distance))
        {
            ChangeState(State.Teleport);
            return true;
        }

        agent.isStopped = false;
        agent.SetDestination(playerTransform.position);
        ChangeState(State.Chase);

        return true;
    }

    private bool ShouldTeleport(float distance)
    {
        if (Time.time < lastTeleportTime + teleportCooldown)
            return false;

        if (distance <= attackRange + 0.5f)
            return false;

        return distance > attackRange + 0.75f ||
               distance > teleportTriggerDistance ||
               HasObstacleBetween(transform.position, playerTransform.position);
    }

    private bool TryGetTeleportDestination(out Vector3 destination)
    {
        destination = transform.position;

        if (playerTransform == null)
            return false;

        for (int i = 0; i < 12; i++)
        {
            float distance = Random.Range(minTeleportDistance, maxTeleportDistance);
            float angle = Random.Range(0f, 360f);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = playerTransform.position + direction * distance;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                continue;

            if (FlatDistance(hit.position, playerTransform.position) > maxTeleportDistance)
                continue;

            if (HasObstacleAtPoint(hit.position))
                continue;

            if (FlatDistance(hit.position, transform.position) < 1.5f)
                continue;

            if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, teleportPath))
                continue;

            if (teleportPath.status != NavMeshPathStatus.PathComplete)
                continue;

            destination = hit.position;
            return true;
        }

        return false;
    }

    private bool HasObstacleAtPoint(Vector3 point)
    {
        if (obstacleLayer.value == 0)
            return false;

        return Physics.CheckSphere(point + Vector3.up * 0.5f, ObstacleCheckRadius, obstacleLayer, QueryTriggerInteraction.Ignore);
    }

    private bool HasObstacleBetween(Vector3 start, Vector3 end)
    {
        if (obstacleLayer.value == 0)
            return false;

        Vector3 direction = end - start;
        float distance = direction.magnitude;

        if (distance <= 0.001f)
            return false;

        return Physics.Raycast(
            start + Vector3.up * 0.5f,
            direction.normalized,
            distance,
            obstacleLayer,
            QueryTriggerInteraction.Ignore);
    }

    private bool HasLineOfSightToPlayer()
    {
        if (playerTransform == null)
            return false;

        if (obstacleLayer.value == 0)
            return true;

        Vector3 rayStart = transform.position + Vector3.up * 0.5f;
        Vector3 rayTarget = playerTransform.position + Vector3.up * 0.5f;
        Vector3 direction = rayTarget - rayStart;

        if (direction.sqrMagnitude < 0.001f)
            return true;

        return !Physics.Raycast(
            rayStart,
            direction.normalized,
            direction.magnitude,
            obstacleLayer,
            QueryTriggerInteraction.Ignore);
    }

    private bool SetRandomPatrolPoint()
    {
        Vector3 randomDirection = Random.insideUnitSphere * patrolRadius;
        randomDirection.y = 0f;

        Vector3 randomPosition = transform.position + randomDirection;

        if (NavMesh.SamplePosition(randomPosition, out NavMeshHit hit, patrolRadius, NavMesh.AllAreas))
        {
            agent.SetDestination(hit.position);
            return true;
        }

        return false;
    }

    private void LookAt(Vector3 target)
    {
        target.y = transform.position.y;
        Vector3 direction = target - transform.position;

        if (direction.sqrMagnitude < 0.001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, 10f * Time.deltaTime);
    }

    private void DoAttack()
    {
        Debug.Log($"Teleport Enemy attacks! Damage: {attackDamage}");

        ApplyDamageToPlayer();

        PlayTemporaryColor(Color.red);
    }

    private void ApplyDamageToPlayer()
    {
        if (playerTransform == null)
            return;

        GameObject playerObject = playerTransform.gameObject;
        playerObject.SendMessage("TakeDamage", attackDamage, SendMessageOptions.DontRequireReceiver);
        playerObject.SendMessage("ApplyDamage", attackDamage, SendMessageOptions.DontRequireReceiver);
        playerObject.SendMessage("ReceiveDamage", attackDamage, SendMessageOptions.DontRequireReceiver);
    }

    private void PlayTemporaryColor(Color color)
    {
        if (enemyRenderer == null)
            return;

        if (colorEffectRoutine != null)
            StopCoroutine(colorEffectRoutine);

        enemyRenderer.material.color = baseRendererColor;
        colorEffectRoutine = StartCoroutine(TemporaryColorRoutine(color));
    }

    private IEnumerator TemporaryColorRoutine(Color color)
    {
        if (enemyRenderer == null)
            yield break;

        enemyRenderer.material.color = color;

        yield return new WaitForSeconds(0.12f);

        enemyRenderer.material.color = baseRendererColor;
        colorEffectRoutine = null;
    }

    private float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, scanRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, maxTeleportDistance);
    }
}