import json, sys
L=[json.loads(l) for l in open(sys.argv[1])]
r=[x for x in L if x['tools']][-1]
s="\n".join(c if isinstance(c,str) else json.dumps(c) for c in r['system'])
i=s.find('# Code Mode'); sec=s[i:] if i>=0 else ''
print('system chars', len(s), '| Code Mode section chars', len(sec), '~tokens', len(sec)//4)
print('tools offered:', [t['function']['name'] for t in r['tools']])
print('tool schema chars', len(json.dumps(r['tools'])), '~tokens', len(json.dumps(r['tools']))//4)
if len(sys.argv)>2: open(sys.argv[2],'w').write(sec)
