#!/bin/bash
# EPG Manager Mac Server Update Script
# Usage: ./update_epgmanager.sh

EPG="$HOME/epg"
GIT="$HOME/epgmanager_git"

echo "======================================="
echo "  EPG Manager Update"
echo "======================================="

# Check Downloads for updated server.py
if [ -f "$HOME/Downloads/server.py" ]; then
  cp "$HOME/Downloads/server.py" "$EPG/server.py"
  echo "Copied server.py from Downloads"
  rm "$HOME/Downloads/server.py"
fi

# Sync to git folder and push
cp "$EPG/server.py" "$GIT/server.py"
cd "$GIT"
git add server.py
git diff --cached --quiet && echo "No changes to commit." && exit 0

DATE=$(date "+%Y.%m.%d %H:%M")
git commit -m "Update $DATE" && git push
echo ""
echo "Done."
