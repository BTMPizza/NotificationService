// NotificationService (.NET 9) CI → GitOps deploy. Same flow as the AuthService and Gateway pipelines.
//
// Flow: build & test the app → build/push an IMMUTABLE image (<short-sha>-<build#>, never "latest")
//       → clone the shared deployment repo → `yq` bump image.repository/image.tag in
//       charts/notificationservice/values.yaml → validate the rendered chart → commit + push.
//       Argo CD (syncPolicy.automated) rolls the change out once the commit lands on main.
//
// charts/notificationservice/values.yaml is the single source of truth for the image; the Argo
// Application carries no helm.valuesObject, and the Validate stage fails the build if one appears.
//
// Required on the Jenkins agent (PATH): dotnet 9 SDK, docker, git, yq (mikefarah v4), helm, kubeconform,
//   and the argocd CLI for the rollout-gate stage at the end.

pipeline {
    agent any

    environment {
        // ---- image (immutable tag computed in the Prepare stage) ----
        REGISTRY   = 'ghcr.io'
        IMAGE_NAME = 'btmpizza/notificationservice'   // must be lowercase for GHCR
        IMAGE_REPO = "${REGISTRY}/${IMAGE_NAME}"       // ghcr.io/btmpizza/notificationservice

        // ---- deployment repo (shared with AuthService and Gateway) ----
        DEPLOY_REPO_URL = 'github.com/BTMPizza/AuthService-deployment.git'
        DEPLOY_BRANCH   = 'main'
        CHART_DIR       = 'charts/notificationservice'
        VALUES_FILE     = 'charts/notificationservice/values.yaml'
        ARGO_APP_FILE   = 'argocd/notificationservice-application.yaml'

        // ---- rendering ----
        K8S_NAMESPACE = 'notificationservice'
        HELM_RELEASE  = 'notification-service'

        // ---- Argo CD (rollout-gate stage) ----
        ARGOCD_SERVER = '192.168.1.24:30454'
        ARGOCD_APP    = 'notification-service'   // Argo Application name

        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }

    options {
        timestamps()
        disableConcurrentBuilds()
        timeout(time: 30, unit: 'MINUTES')
    }

    stages {

        stage('Checkout & prepare') {
            steps {
                checkout scm
                script {
                    // Unique per build but traceable to the source commit: <short-sha>-<build#>.
                    def shortSha = sh(returnStdout: true, script: 'git rev-parse --short HEAD').trim()
                    env.IMAGE_TAG = "${shortSha}-${env.BUILD_NUMBER}"
                    env.IMAGE_REF = "${env.IMAGE_REPO}:${env.IMAGE_TAG}"
                    echo "Building image ${env.IMAGE_REF}"
                }
            }
        }

        stage('Build') {
            steps {
                sh 'dotnet --info'
                sh 'dotnet restore BtmPizza.Notifications.sln'
                sh 'dotnet build BtmPizza.Notifications.sln -c Release --no-restore'
            }
        }

        stage('Test') {
            steps {
                // Self-contained: the tests use a temp credentials file, a temp SQLite db and a fake
                // HTTP/2 APNs server on loopback, so no RabbitMQ / Apple access is needed on the agent.
                sh 'dotnet test BtmPizza.Notifications.sln -c Release --no-build --logger "trx;LogFileName=test-results.trx"'
            }
            post {
                always {
                    archiveArtifacts artifacts: '**/TestResults/*.trx', allowEmptyArchive: true
                }
            }
        }

        stage('Image build & push') {
            steps {
                // GitHub PAT (repo + write:packages) — authenticates to GHCR and the deployment repo.
                withCredentials([usernamePassword(credentialsId: '146c0e33-3abf-428c-9f3b-d67d47d6010f',
                                                   usernameVariable: 'REG_USER',
                                                   passwordVariable: 'REG_PASS')]) {
                    sh '''
                        echo "$REG_PASS" | docker login "$REGISTRY" -u "$REG_USER" --password-stdin
                        docker build -t "$IMAGE_REF" .
                        docker push "$IMAGE_REF"
                    '''
                }
            }
        }

        stage('Deploy-repo checkout') {
            steps {
                withCredentials([usernamePassword(credentialsId: '146c0e33-3abf-428c-9f3b-d67d47d6010f',
                                                   usernameVariable: 'GIT_USER',
                                                   passwordVariable: 'GIT_TOKEN')]) {
                    sh '''
                        rm -rf deploy-repo
                        git clone --branch "$DEPLOY_BRANCH" \
                          "https://${GIT_USER}:${GIT_TOKEN}@${DEPLOY_REPO_URL}" deploy-repo
                    '''
                }
            }
        }

        stage('yq bump') {
            steps {
                dir('deploy-repo') {
                    sh '''
                        REPO="$IMAGE_REPO" TAG="$IMAGE_TAG" \
                          yq -i '.image.repository = strenv(REPO) | .image.tag = strenv(TAG)' "$VALUES_FILE"
                        echo "values.yaml now:"; yq '.image' "$VALUES_FILE"
                    '''
                }
            }
        }

        stage('Validate') {
            steps {
                dir('deploy-repo') {
                    sh '''
                        set -e

                        # If the Argo Application overrides image.* via valuesObject, the values.yaml bump
                        # would be inert and Argo would keep deploying the old image.
                        if [ -f "$ARGO_APP_FILE" ]; then
                            OVERRIDE=$(yq '.spec.source.helm.valuesObject.image // "null"' "$ARGO_APP_FILE")
                            if [ "$OVERRIDE" != "null" ]; then
                                echo "ERROR: $ARGO_APP_FILE sets spec.source.helm.valuesObject.image."
                                echo "       Helm valuesObject overrides values.yaml, so this bump has no effect."
                                exit 1
                            fi
                        fi

                        # Chart must lint and render to schema-valid Kubernetes manifests.
                        helm lint "$CHART_DIR"
                        helm template "$HELM_RELEASE" "$CHART_DIR" -n "$K8S_NAMESPACE" \
                          | kubeconform -strict -summary -

                        # The rendered Deployment image must equal the tag we just pushed.
                        RENDERED=$(helm template "$HELM_RELEASE" "$CHART_DIR" -n "$K8S_NAMESPACE" \
                          | yq 'select(.kind == "Deployment") | .spec.template.spec.containers[0].image' -)
                        echo "rendered image: $RENDERED  expected: $IMAGE_REF"
                        if [ "$RENDERED" != "$IMAGE_REF" ]; then
                            echo "ERROR: rendered Deployment image does not match the pushed tag."
                            exit 1
                        fi
                    '''
                }
            }
        }

        stage('Commit & push') {
            steps {
                dir('deploy-repo') {
                    sh '''
                        git config user.email "jenkins@btm.local"
                        git config user.name  "jenkins-ci"
                        git add "$VALUES_FILE"
                        if git diff --cached --quiet; then
                            echo "No image change — nothing to commit."
                        else
                            git commit -m "ci: bump notificationservice image to ${IMAGE_TAG}"
                            # The AuthService and Gateway pipelines push to this repo too; replay our
                            # one-file bump on top of anything they landed since our clone.
                            for attempt in 1 2 3; do
                                git pull --rebase origin "$DEPLOY_BRANCH" && git push origin "$DEPLOY_BRANCH" && break
                                [ "$attempt" = 3 ] && exit 1
                                sleep 5
                            done
                        fi
                    '''
                }
            }
        }

        // Rollout gate: Argo auto-syncs on the pushed commit; this blocks until it is Synced & Healthy.
        stage('Verify Argo rollout') {
            steps {
                withCredentials([string(credentialsId: 'argocd-jenkins-token', variable: 'ARGOCD_TOKEN')]) {
                    // $ARGOCD_TOKEN stays a shell variable (single quotes) so it never reaches the build log.
                    sh '''
                        ARGS="--server $ARGOCD_SERVER --plaintext --auth-token $ARGOCD_TOKEN"
                        argocd app sync "$ARGOCD_APP" $ARGS
                        argocd app wait "$ARGOCD_APP" --sync --health --timeout 300 $ARGS
                        argocd app get  "$ARGOCD_APP" $ARGS
                    '''
                }
            }
        }
    }

    post {
        success { echo "Deployed ${IMAGE_REF}; Argo app 'notification-service' bumped via values.yaml." }
        failure { echo "Pipeline failed — image ${IMAGE_REF} may have been pushed; check the failing stage." }
        always  { sh 'docker logout "$REGISTRY" || true' }
    }
}
