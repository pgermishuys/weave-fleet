#!/usr/bin/env bash
# Release guard: a tag publishes only if CI (ci.yml) passed on the very commit it points at.
#
# Waits while ci.yml is still running on the commit, then passes only when a push or workflow_dispatch run of it on
# that commit succeeded and, when the run has a "CI passed" job, that job succeeded too. Pull request runs don't
# count: they test the PR merged into main, not this commit. A red or cancelled run fails at once, and so does a
# commit CI never ran on (after a few minutes' grace for the run to show up). Each failure says how to recover.
#
# There's no stand-in: ci.yml runs on every push to main, docs-only ones too, so the tagged commit itself needs its run.
#
# Before that, for a real tag push (GATE_CHECK_TAG, on unless "false"): the tag must be Directory.Build.props' version
# on the tagged commit with a "v" in front, and the commit must be on main (main itself or an ancestor of it). A dry run
# by hand on a branch sets GATE_CHECK_TAG=false and gets a note that these were skipped.
#
# Usage: release-ci-gate.sh <commit-ish> [<tag>]   (run inside a clone that has the commit)
# Env:   GH_REPO (owner/name, required), GH_TOKEN (for gh), GATE_TIMEOUT_SECONDS (default 3600),
#        GATE_NO_RUN_GRACE_SECONDS (default 300), GATE_POLL_SECONDS (default 30), GATE_CI_WORKFLOW (default ci.yml),
#        GATE_REQUIRED_JOB (default "CI passed"), GATE_RETRY_SECONDS (API retry back-off step, default 5),
#        GATE_CHECK_TAG (default true), GATE_MAIN_BRANCH (default main).
set -euo pipefail
# Command substitutions fail the script too, so an API error is never read as "no runs".
shopt -s inherit_errexit

# A tag name or a short SHA works too: the API only matches a full commit SHA.
sha="$(git rev-parse --verify "${1:?usage: release-ci-gate.sh <commit-ish> [<tag>]}^{commit}")"
tag="${2:-}"
repo="${GH_REPO:?GH_REPO must be owner/name}"
workflow="${GATE_CI_WORKFLOW:-ci.yml}"
required_job="${GATE_REQUIRED_JOB:-CI passed}"
timeout_seconds="${GATE_TIMEOUT_SECONDS:-3600}"
grace_seconds="${GATE_NO_RUN_GRACE_SECONDS:-300}"
poll_seconds="${GATE_POLL_SECONDS:-30}"
retry_seconds="${GATE_RETRY_SECONDS:-5}"
check_tag="${GATE_CHECK_TAG:-true}"
main_branch="${GATE_MAIN_BRANCH:-main}"

summary() {
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$*" >> "$GITHUB_STEP_SUMMARY"
  fi
}

fail() {
  echo "::error title=Release blocked::$1"
  summary "**Release blocked:** $1"
  shift
  for line in "$@"; do
    echo "$line"
    summary "$line"
  done
  exit 1
}

# The steps to get the release going again, written for whoever (or whichever agent) reads the log, without the workflow.
# $1: the case (failed, cancelled, never-ran, still-running); $2: the CI run ids it's about.
recovery() {
  local case="$1" ids="$2" ref="${tag:-<tag>}" id
  local release_run="${GITHUB_RUN_ID:-<this Release run id>}"
  local run_ci="gh workflow run ${workflow} --repo ${repo} --ref ${ref}"
  local watch_ci="gh run list --repo ${repo} --workflow ${workflow} --commit ${sha}   # then: gh run watch <id> --repo ${repo} --exit-status"
  echo ""
  echo "Nothing was built or published. Don't move, delete or re-push the tag, and don't publish anything by hand."
  echo ""
  case "$case" in
    failed)
      echo "What to do:"
      echo "  1. Read why CI failed:"
      for id in $ids; do echo "       gh run view ${id} --repo ${repo} --log-failed"; done
      echo "  2. If a check failed because of the code, this version can't ship. Stop here: fix it on main in a PR, then"
      echo "     bump the version and tag that. Leave this tag as it is; it was never released."
      echo "     If it's a known flaky test or the runner broke (lost runner, network, timeout), re-run the failed jobs:"
      for id in $ids; do echo "       gh run rerun ${id} --repo ${repo} --failed"; done
      echo "     and wait for that run to finish green:"
      for id in $ids; do echo "       gh run watch ${id} --repo ${repo} --exit-status"; done
      ;;
    cancelled)
      echo "What to do: the run was cancelled (usually a newer push to main), not failed. Run CI again on the tag:"
      echo "  1. ${run_ci}"
      echo "  2. Wait for it to finish green: ${watch_ci}"
      ;;
    never-ran)
      echo "What to do: run CI on the tag:"
      echo "  1. ${run_ci}"
      echo "  2. Wait for it to finish green: ${watch_ci}"
      ;;
    still-running)
      echo "What to do: wait for the run to finish green:"
      for id in $ids; do echo "  1. gh run watch ${id} --repo ${repo} --exit-status"; done
      echo "  If it's stuck, cancel it (gh run cancel <id> --repo ${repo}) and run CI on the tag: ${run_ci}"
      ;;
  esac
  echo "  3. Once CI is green on ${sha}, let this release carry on (it re-runs this check, then builds and publishes):"
  echo "       gh run rerun ${release_run} --repo ${repo} --failed"
}

# gh api with a few retries, so one blip of GitHub's API doesn't block a release; still failing, it exits non-zero.
api() {
  local attempt
  for attempt in 1 2 3 4; do
    if gh api "$@"; then
      return 0
    fi
    echo "gh api $1 failed (attempt ${attempt}); retrying." >&2
    sleep $(( attempt * retry_seconds ))
  done
  gh api "$@"
}

# Prints "<id> <status> <conclusion> <event>" for each push or workflow_dispatch run of the CI workflow on a commit.
ci_runs() {
  api "repos/${repo}/actions/workflows/${workflow}/runs?head_sha=$1&per_page=100" \
    --jq '.workflow_runs[] | select(.event == "push" or .event == "workflow_dispatch")
          | "\(.id) \(.status) \(.conclusion // "none") \(.event)"'
}

# Prints the conclusion of the required job in a run, or nothing when the run has no such job.
required_job_conclusion() {
  api "repos/${repo}/actions/runs/$1/jobs?filter=latest&per_page=100" --paginate \
    --jq ".jobs[] | select(.name == \"${required_job}\") | (.conclusion // .status)"
}

# The tag must name the version the commit builds, and the commit must be on main. Only for a real tag push.
check_tag_and_branch() {
  if [ "$check_tag" = "false" ]; then
    echo "::notice::Dry run: skipped the tag checks (tag = Directory.Build.props' version, commit on ${main_branch})."
    summary "Dry run: skipped the tag checks (the tag matches Directory.Build.props' version, the commit is on ${main_branch})."
    return
  fi
  local leave="Nothing was built or published."
  [ -n "$tag" ] || fail "No tag given: a release needs the tag to check it against Directory.Build.props." "$leave"

  local props version
  props="$(git show "${sha}:Directory.Build.props" 2>/dev/null)" ||
    fail "${sha} has no Directory.Build.props, so there's no version to check ${tag} against." "$leave"
  version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' <<< "$props")"
  version="${version%%$'\n'*}"
  if [ "v${version}" != "$tag" ]; then
    fail "The tag ${tag} doesn't match the version on its commit: Directory.Build.props at ${sha} says ${version:-nothing}." \
      "$leave" \
      "What to do: the release commit must bump Directory.Build.props to ${tag#v} (see RELEASE.md, Release order)." \
      "  1. Remove the wrong tag: git push origin :refs/tags/${tag} && git tag -d ${tag}" \
      "  2. Bump the version on main if it isn't yet, then tag the commit that has it: git tag -a vX.Y.Z -m vX.Y.Z <commit>"
  fi

  # GitHub's compare: "identical" (the commit is main) or "behind" (main contains it) is on main.
  local status
  status="$(api "repos/${repo}/compare/${main_branch}...${sha}" --jq .status)"
  case "$status" in
    identical | behind) echo "${tag} is ${version} on ${sha}, which is on ${main_branch}." ;;
    *)
      fail "${sha} (${tag}) isn't on ${main_branch} (compared with ${main_branch}: ${status}). Releases come only from ${main_branch}." \
        "$leave" \
        "What to do: get the change merged to ${main_branch}, then tag ${main_branch}'s commit:" \
        "  1. Remove this tag: git push origin :refs/tags/${tag} && git tag -d ${tag}" \
        "  2. Tag the release commit on ${main_branch}: git tag -a ${tag} -m ${tag} <commit on ${main_branch}> && git push origin ${tag}"
      ;;
  esac
}

check_tag_and_branch

start="$(date +%s)"
while :; do
  elapsed=$(( $(date +%s) - start ))
  runs="$(ci_runs "$sha")"

  if [ -z "$runs" ]; then
    if [ "$elapsed" -ge "$grace_seconds" ]; then
      fail "${workflow} never ran on ${sha}${tag:+ (${tag})}, so nothing shows it passed." \
        "Only push and workflow_dispatch runs on this exact commit count (pull request runs test a merge commit)." \
        "$(recovery never-ran "")"
    fi
    echo "No ${workflow} run on ${sha} yet (${elapsed}s); waiting up to ${grace_seconds}s for one to start."
    sleep "$poll_seconds"
    continue
  fi

  passed_run=""
  pending=""
  verdicts=""
  failed_ids=""
  open_ids=""
  while read -r id status conclusion event; do
    [ -z "$id" ] && continue
    if [ "$status" != "completed" ]; then
      pending=1
      open_ids+="${id} "
      verdicts+="  run ${id} (${event}): ${status}"$'\n'
      continue
    fi
    if [ "$conclusion" = "success" ]; then
      job="$(required_job_conclusion "$id")"
      if [ -z "$job" ] || [ "$job" = "success" ]; then
        passed_run="$id"
        break
      fi
      verdicts+="  run ${id} (${event}): success, but its \"${required_job}\" job is ${job}"$'\n'
      failed_ids+="${id} "
      continue
    fi
    verdicts+="  run ${id} (${event}): ${conclusion}"$'\n'
    [ "$conclusion" = "cancelled" ] || failed_ids+="${id} "
  done <<< "$runs"

  if [ -n "$passed_run" ]; then
    url="https://github.com/${repo}/actions/runs/${passed_run}"
    echo "${workflow} passed on ${sha}: ${url}"
    summary "CI passed on \`${sha}\`${tag:+ (${tag})}: ${url}"
    exit 0
  fi

  if [ -z "$pending" ]; then
    fail "${workflow} didn't pass on ${sha}${tag:+ (${tag})}." "${verdicts%$'\n'}" \
      "$(if [ -n "$failed_ids" ]; then recovery failed "$failed_ids"; else recovery cancelled ""; fi)"
  fi

  if [ "$elapsed" -ge "$timeout_seconds" ]; then
    fail "${workflow} on ${sha} was still running after ${timeout_seconds}s." "${verdicts%$'\n'}" \
      "$(recovery still-running "$open_ids")"
  fi

  echo "${workflow} on ${sha} is still running (${elapsed}s):"
  printf '%s' "$verdicts"
  sleep "$poll_seconds"
done
