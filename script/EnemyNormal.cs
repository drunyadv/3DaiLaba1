using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class NormalEnemyAI : MonoBehaviour
{
    public enum State
    {
        Idle,
        Patrol,
        Chase,
        Attack,
        Search
    }

    [Header("State")]
    [SerializeField] private State currentState = State.Patrol;

    [Header("References")]
    [SerializeField] private Transform playerTransform;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;

    [Header("Patrol")]
    [SerializeField] private float patrolRadius = 8f;
    [SerializeField] private float waitAtPatrolPoint = 1.5f;

    [Header("Vision")]
    [SerializeField] private float viewDistance = 7f;
    [SerializeField] private float viewAngle = 180f;
    [SerializeField] private float loseSightDelay = 0.4f;
    [SerializeField] private LayerMask obstacleLayer;

    [Header("Attack")]
    [SerializeField] private float attackRange = 1.2f;
    [SerializeField] private float attackCooldown = 1.5f;
    [SerializeField] private float firstAttackDelay = 0.35f;
    [SerializeField] private int attackDamage = 10;

    [Header("Search")]
    [SerializeField] private float searchDuration = 3f;

    [Header("Navigation")]
    [SerializeField] private float stuckVelocityThreshold = 0.08f;
    [SerializeField] private float stuckTimeBeforeSearch = 1f;

    private NavMeshAgent agent;
    private Renderer enemyRenderer;

    private Vector3 lastKnownPos;

    private float stateTimer;
    private float lastAttackTime = -999f;
    private float firstAttackTime;
    private float lostSightTimer;
    private float stuckTimer;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        enemyRenderer = GetComponentInChildren<Renderer>();

        if (agent == null)
        {
            Debug.LogError("NormalEnemyAI requires a NavMeshAgent component.");
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
        }
    }

    private void ChangeState(State newState)
    {
        currentState = newState;

        if (newState != State.Chase)
            lostSightTimer = 0f;

        if (newState != State.Chase && newState != State.Search)
            stuckTimer = 0f;

        if (newState == State.Search)
            stateTimer = searchDuration;

        if (newState == State.Idle)
            stateTimer = waitAtPatrolPoint;

        if (newState == State.Attack)
            firstAttackTime = Time.time + firstAttackDelay;
    }

    private void IdleUpdate()
    {
        agent.isStopped = true;

        if (CanSeePlayer())
        {
            ChangeState(State.Chase);
            return;
        }

        stateTimer -= Time.deltaTime;

        if (stateTimer <= 0f)
            ChangeState(State.Patrol);
    }

    private void PatrolUpdate()
    {
        agent.isStopped = false;

        if (CanSeePlayer())
        {
            ChangeState(State.Chase);
            return;
        }

        if (!agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.2f)
        {
            SetRandomPatrolPoint();
        }

        if (agent.pathPending)
            return;

        if (agent.remainingDistance <= agent.stoppingDistance + 0.2f && agent.velocity.sqrMagnitude < 0.05f)
        {
            ChangeState(State.Idle);
        }
    }

    private void ChaseUpdate()
    {
        agent.isStopped = false;

        if (playerTransform == null)
        {
            ChangeState(State.Patrol);
            return;
        }

        if (CanSeePlayer())
        {
            lostSightTimer = 0f;
            lastKnownPos = playerTransform.position;

            float distance = FlatDistance(transform.position, playerTransform.position);

            if (distance <= attackRange)
            {
                ChangeState(State.Attack);
                return;
            }

            agent.SetDestination(playerTransform.position);

            if (IsAgentStuck(distance))
            {
                ChangeState(State.Search);
                return;
            }

            return;
        }

        lostSightTimer += Time.deltaTime;
        agent.SetDestination(lastKnownPos);

        if (IsAgentStuck(FlatDistance(transform.position, lastKnownPos)) || lostSightTimer >= loseSightDelay)
        {
            ChangeState(State.Search);
        }
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

        if (!HasLineOfSightToPlayer())
        {
            lastKnownPos = playerTransform.position;
            ChangeState(State.Search);
            return;
        }

        if (distance > attackRange)
        {
            ChangeState(State.Chase);
            return;
        }

        LookAt(playerTransform.position);

        if (Time.time >= firstAttackTime && Time.time >= lastAttackTime + attackCooldown)
        {
            DoAttack();
            lastAttackTime = Time.time;
        }
    }

    private void SearchUpdate()
    {
        agent.isStopped = false;

        if (CanSeePlayer())
        {
            ChangeState(State.Chase);
            return;
        }

        agent.SetDestination(lastKnownPos);

        bool reachedLastKnown = FlatDistance(transform.position, lastKnownPos) <= 0.3f;
        bool badPath = !agent.pathPending
            && (agent.pathStatus == NavMeshPathStatus.PathInvalid
                || agent.pathStatus == NavMeshPathStatus.PathPartial);
        bool stuck = IsAgentStuck(FlatDistance(transform.position, lastKnownPos));

        if (reachedLastKnown || badPath || stuck)
        {
            agent.isStopped = true;

            stateTimer -= Time.deltaTime;
            transform.Rotate(0f, 120f * Time.deltaTime, 0f);

            if (stateTimer <= 0f)
                ChangeState(State.Patrol);
        }
    }

    private bool CanSeePlayer()
    {
        if (playerTransform == null)
            return false;

        Vector3 toPlayer = playerTransform.position - transform.position;
        toPlayer.y = 0f;

        float distance = toPlayer.magnitude;
        if (distance > viewDistance)
            return false;

        float angle = Vector3.Angle(transform.forward, toPlayer);
        if (angle > viewAngle * 0.5f)
            return false;

        return HasLineOfSightToPlayer();
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

        return !Physics.Raycast(
            rayStart,
            direction.normalized,
            direction.magnitude,
            obstacleLayer,
            QueryTriggerInteraction.Ignore);
    }

    private bool IsAgentStuck(float distanceToTarget)
    {
        if (agent.pathPending)
            return false;

        if (distanceToTarget <= agent.stoppingDistance + 0.4f)
        {
            stuckTimer = 0f;
            return false;
        }

        if (agent.velocity.magnitude <= stuckVelocityThreshold)
            stuckTimer += Time.deltaTime;
        else
            stuckTimer = 0f;

        return stuckTimer >= stuckTimeBeforeSearch;
    }

    private void SetRandomPatrolPoint()
    {
        Vector3 randomDirection = Random.insideUnitSphere * patrolRadius;
        randomDirection.y = 0f;

        Vector3 randomPosition = transform.position + randomDirection;

        if (NavMesh.SamplePosition(randomPosition, out NavMeshHit hit, patrolRadius, NavMesh.AllAreas))
        {
            agent.SetDestination(hit.position);
        }
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
        Debug.Log($"Enemy attacks! Damage: {attackDamage}");

        if (enemyRenderer != null)
            StartCoroutine(Flash());
    }

    private IEnumerator Flash()
    {
        if (enemyRenderer == null)
            yield break;

        Color originalColor = enemyRenderer.material.color;
        enemyRenderer.material.color = Color.red;

        yield return new WaitForSeconds(0.12f);

        enemyRenderer.material.color = originalColor;
    }

    private float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, viewDistance);

        Vector3 forward = transform.forward;
        Vector3 left = Quaternion.Euler(0f, -viewAngle * 0.5f, 0f) * forward;
        Vector3 right = Quaternion.Euler(0f, viewAngle * 0.5f, 0f) * forward;

        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, left * viewDistance);
        Gizmos.DrawRay(transform.position, right * viewDistance);
        Gizmos.DrawRay(transform.position, forward * viewDistance);
    }
}