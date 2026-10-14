#!/usr/bin/env bash
# git credential helper used by push/pull: username = x-access-token, password = the GitHub token.
# GIT_TOKEN is provided by fly as a secret; this never writes it anywhere.
case "$1" in
  *[Uu]sername*) echo "x-access-token" ;;
  *[Pp]assword*) echo "${GIT_TOKEN}" ;;
esac
