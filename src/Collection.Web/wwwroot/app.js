'use strict';
const $=s=>document.querySelector(s), esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let state, view='items', filter='', selected=null, edit=null, list=false, page=1, dirty=false, saveRevision=0;
let remoteClient=false, renderSequence=0, openSequence=0, inboundRows=[], inboundPage=0, inboundTotal=0, refOptions=[];
const cache=new Map(), checked=new Map();
function remember(items){for(const i of items)cache.set(i.id,i);}
function setCatalog(c){state={...c,items:state?.items||[]};}
async function reloadCatalog(){setCatalog(await api('/catalog'));}
function newId(){
 if(crypto.randomUUID)return crypto.randomUUID();
 const bytes=crypto.getRandomValues(new Uint8Array(16));bytes[6]=(bytes[6]&15)|64;bytes[8]=(bytes[8]&63)|128;
 return [...bytes].map(b=>b.toString(16).padStart(2,'0')).join('');
}
function menu(open){document.body.classList.toggle('menu-open',open);$('#mobile-menu').setAttribute('aria-expanded',String(open));$('#menu-backdrop').hidden=!open;}
function closeDetail(){if(!leave())return;dirty=false;edit=null;selected=null;document.body.classList.remove('detail-open');$('#detail').innerHTML='<p class="muted">选择收藏以查看详情。</p>';render();}
async function acceptImport(r){setCatalog(r.catalog);view='drafts';filter='';page=1;$('#search').value='';dirty=false;edit=null;selected=null;document.body.classList.remove('detail-open');menu(false);$('#detail').innerHTML='<p class="muted">选择候选对象开始审核。</p>';await render();notify(`已暂存 ${r.summary.added} 项`);}

function uploadFile(){
 if(!leave())return;
 const revision=state.revision;
 modal('<h2>上传到收藏库</h2><p class="muted">文件将复制到电脑的收藏数据目录，标签与属性先进入暂存区，等待确认。单个文件最多 100 MB。</p><label class="field"><span>选择手机或电脑中的文件</span><input id="upload-file" type="file" required></label>',async()=>{
  const file=$('#upload-file').files[0];if(!file)throw Error('请选择文件。');if(file.size>100*1024*1024)throw Error('单个文件最多 100 MB；较大视频请在电脑端按路径导入。');
  const body=new FormData();body.append('revision',revision);body.append('file',file);
  const r=await fetch('/api/upload?compact=1',{method:'POST',headers:{'X-Collection-Client':'local-ui'},body});
  if(!r.ok){const error=await r.json().catch(()=>({error:'上传失败，请检查文件大小和连接。'}));throw Error(error.error);}
  await acceptImport(await r.json());
 });
}
const kinds={text:'文本',number:'数字',ref:'Item 引用',refs:'Item 数组'};
const tag=id=>state.tags.find(t=>t.id===id), item=id=>cache.get(id);
function effective(i){let out=new Set();for(let id of i.tags){while(id&&!out.has(id)){out.add(id);id=tag(id)?.parentId;}}return [...out];}
function notify(text){$('#toast').textContent=text;$('#toast').style.display='block';clearTimeout(notify.timer);notify.timer=setTimeout(()=>$('#toast').style.display='none',5500);}
async function api(path,method='GET',body){const r=await fetch('/api'+path+(method==='GET'?'':(path.includes('?')?'&':'?')+'compact=1'),{method,headers:{'Content-Type':'application/json','X-Collection-Client':'local-ui'},body:body===undefined?undefined:JSON.stringify(body)});if(!r.ok){let e=await r.json().catch(()=>({error:'请求失败'}));throw Error(e.error||'请求失败');}return r.status===204?null:r.json();}
async function refresh(){const client=await api('/client');remoteClient=client.remote;$('#import').hidden=remoteClient;$('#logout').hidden=!remoteClient;await reloadCatalog();await render();if(!remoteClient)pollJobs();}
function leave(){return !dirty||confirm('有未保存的修改，放弃这些修改？');}
async function render(){
 const sequence=++renderSequence;
 $('#count').textContent=state.count;$('#draft-count').textContent=state.draftCount;$('#mobile-draft-count').textContent=state.draftCount;
 const categories=state.tags.filter(t=>t.properties.length||state.counts[t.id]||['image','anime','av','later','actor'].includes(t.id));
 $('#tags').innerHTML=categories.map(t=>`<button data-filter="${esc(t.id)}" class="${filter===t.id?'active':''}">${t.parentId?'↳ ':''}${esc(t.name)} <span>${state.counts[t.id]||0}</span></button>`).join('');
 document.querySelectorAll('[data-view]').forEach(b=>b.classList.toggle('active',b.dataset.view===view&&!filter));
 $('#title').textContent=view==='manage'?'标签与属性':view==='drafts'?'导入暂存区':filter?tag(filter)?.name:'全部收藏';
 $('#subtitle').textContent=view==='drafts'?'选择候选后可批量调整标签、确认或放弃。':view==='manage'?'同一个标签，既可分类，也可作为属性名。':'独立对象 · 类型标签 · 彼此关联';
 $('#toggle').hidden=view==='manage';
 if(view==='manage'){renderTags();return;}
 try{
 const result=await api('/items?'+new URLSearchParams({draft:String(view==='drafts'),q:$('#search').value,tag:filter,offset:String((page-1)*60),limit:'60'}));
 if(sequence!==renderSequence)return;
 state.items=result.items;remember(result.items);
 $('#content').innerHTML=(view==='drafts'?`<div class="notice">删除候选不会删除原文件。批量操作全部成功或全部不生效。</div><div class="batch-toolbar"><button id="select-page">选择本页</button><button id="clear-selection">清除选择</button><span id="selection-count">已选 ${checked.size} 项</span><button id="batch-tags">调整标签</button><button id="batch-confirm">确认所选</button><button id="batch-discard">放弃所选</button></div>`:'')+(result.total?`<div class="${list?'list':'grid'}">${result.items.map(i=>(view==='drafts'?`<div class="draft-card"><label><input type="checkbox" data-select="${esc(i.id)}" ${checked.has(i.id)?'checked':''}> 选择</label>${card(i)}</div>`:card(i))).join('')}</div><div class="pagination"><button id="previous" ${page===1?'disabled':''}>上一页</button><span>第 ${page} 页 · 共 ${result.total} 项</span><button id="more" ${page*60>=result.total?'disabled':''}>下一页</button></div>`:'<div class="empty"><h2>这里还没有收藏</h2><p>新建一条记录，或从本地文件导入。</p></div>');
 }catch(e){if(sequence===renderSequence)notify(e.message);}
}

function card(i){const tags=effective(i),cover=i.values.cover?.refs[0],pic=!i.draft&&tags.includes('file')&&tags.includes('image')?i.id:cover;const icon=tags.includes('movie')?'◉':tags.includes('video')?'▷':tags.includes('actor')?'♙':tags.includes('image')?'▧':tags.includes('web')?'↗':'≡';return `<button class="tile ${selected===i.id?'selected':''}" data-item="${esc(i.id)}"><div class="art">${pic?`<img loading="lazy" src="/api/items/${encodeURIComponent(pic)}/thumbnail" alt="${esc(i.name)}">`:icon}</div><div class="tile-info"><strong>${esc(i.name)}</strong><div class="pills">${i.tags.map(t=>`<span class="pill">${esc(tag(t)?.name)}</span>`).join('')||'<span class="muted">未分类</span>'}</div></div></button>`;}
async function openItem(id){
 if(!leave())return;const sequence=++openSequence;
 try{const [detail,refs,inbound]=await Promise.all([api('/items/'+encodeURIComponent(id)),api('/items?limit=30'),api('/items/'+encodeURIComponent(id)+'/inbound')]);
 if(sequence!==openSequence)return;remember([detail.item,...detail.related,...refs.items,...inbound.items]);refOptions=refs.items;inboundRows=inbound.items;inboundPage=0;inboundTotal=inbound.total;
 selected=id;edit=structuredClone(detail.item);saveRevision=state.revision;dirty=false;render();renderDetail();
 }catch(e){notify(e.message);}
}

function collect(){if(!edit)return;edit.name=$('#item-name').value;edit.tags=[...document.querySelectorAll('[data-tag-check]:checked')].map(e=>e.dataset.tagCheck);document.querySelectorAll('[data-value]').forEach(e=>{
 const key=e.dataset.value;
 // Empty suggested inputs are placeholders, not stored attributes.
 if(e.value===''){delete edit.values[key];return;}
 edit.values[key]=tag(key).kind==='number'?{number:Number(e.value),refs:[]}:{text:e.value,refs:[]};
});}
function renderDetail(){if(!edit)return;menu(false);document.body.classList.add('detail-open');const tags=effective(edit);const suggested=[...new Set(tags.flatMap(t=>tag(t)?.properties||[]))];const props=[...new Set([...Object.keys(edit.values),...suggested])];const inbound=inboundRows.filter(i=>i.id!==edit.id);
 $('#detail').innerHTML=`<div class="mobile-detail-bar mobile-only"><button id="detail-back">← 返回列表</button><span>收藏详情</span></div>${edit.draft?'<div class="notice">待确认 · 修改建议后点击“确认入库”</div>':''}<p class="eyebrow">${edit.draft?'IMPORT REVIEW':'ITEM DETAILS'}</p><label class="field"><span>显示名称</span><input id="item-name" maxlength="300" value="${esc(edit.name)}"></label><h3>是什么</h3><div class="checks">${state.tags.map(t=>`<label><input type="checkbox" data-tag-check="${esc(t.id)}" ${edit.tags.includes(t.id)?'checked':''}>${esc(t.name)}</label>`).join('')}</div><p class="muted">继承：${tags.filter(t=>!edit.tags.includes(t)).map(t=>esc(tag(t)?.name)).join('、')||'无'}</p><h3 class="section-title">有什么</h3><div id="props">${props.map(k=>property(k)).join('')}</div><label class="field"><span>添加其他属性</span><select id="add-prop"><option value="">选择属性标签…</option>${state.tags.filter(t=>!props.includes(t.id)).map(t=>`<option value="${esc(t.id)}">${esc(t.name)} · ${kinds[t.kind]}</option>`).join('')}</select></label><div class="actions"><button class="primary" id="save">${edit.draft?'保存暂存修改':'保存修改'}</button>${edit.draft?'<button class="primary" id="approve">确认入库</button>':''}<button id="delete" class="danger">${edit.draft?'放弃候选':'删除记录'}</button></div>${!edit.draft&&tags.includes('file')?`<h3 class="section-title">文件预览</h3>${tags.includes('image')?`<img class="preview" src="/api/items/${encodeURIComponent(edit.id)}/media" alt="图片不可用，请检查路径">`:tags.includes('video')?`<video controls preload="metadata" src="/api/items/${encodeURIComponent(edit.id)}/media"></video><p class="muted">能否播放取决于浏览器编码支持；MKV 等可定位后用外部播放器打开。</p>`:''}${remoteClient?'':'<button id="locate">在资源管理器定位</button>'}`:''}<h3 class="section-title">谁引用了我</h3>${inbound.map(i=>`<div class="ref-row"><button data-jump="${esc(i.id)}">${esc(i.name)} ${i.draft?'（暂存）':''}</button></div>`).join('')||'<p class="muted">暂无引用</p>'}${inboundTotal>60?'<button id="inbound-more">下一页引用</button>':''}<p class="source">ID：${esc(edit.id)}<br>${edit.source?'导入来源：'+esc(edit.source):''}</p>`;
}
function property(k){const t=tag(k),v=edit.values[k]||{refs:[]};if(!t)return '';let control;if(t.kind==='text')control=`<textarea rows="2" data-value="${esc(k)}">${esc(v.text||'')}</textarea>`;if(t.kind==='number')control=`<input type="number" step="any" data-value="${esc(k)}" value="${v.number??''}">`;if(t.kind==='ref'||t.kind==='refs')control=`${(v.refs||[]).map((r,index)=>`<div class="ref-row"><button type="button" data-jump="${esc(r)}">${esc(item(r)?.name||r)}</button>${t.kind==='refs'?`<button data-up="${esc(k)}" data-index="${index}" aria-label="向上移动">↑</button>`:''}<button data-unref="${esc(k)}" data-index="${index}" aria-label="移除引用">×</button></div>`).join('')}<input data-ref-search="${esc(k)}" placeholder="搜索要引用的对象"><select data-ref="${esc(k)}"><option value="">添加引用…</option>${refOptions.filter(i=>i.id!==edit.id&&!i.draft&&!(v.refs||[]).includes(i.id)).map(i=>`<option value="${esc(i.id)}">${esc(i.name)}</option>`).join('')}</select>`;return `<div class="field-row"><label class="field"><span>${esc(t.name)} · ${kinds[t.kind]}</span>${control}</label><button data-remove="${esc(k)}" class="remove" aria-label="清空属性">×</button></div>`;}
function modal(html,submit){$('#modal-content').innerHTML=html;$('#modal-form').onsubmit=async e=>{e.preventDefault();const b=$('#modal-form button[type=submit]');b.disabled=true;try{await submit();$('#modal').close();}catch(e){notify(e.message);}finally{b.disabled=false;}};$('#modal').showModal();}
function renderTags(){ $('#content').innerHTML='<button class="primary" id="new-tag">＋ 新建标签</button>'+state.tags.map(t=>`<div class="tag-row"><div><strong>${esc(t.name)}</strong><br><small>${kinds[t.kind]}${t.parentId?' · 父标签：'+esc(tag(t.parentId)?.name):''} · ${t.properties.length} 个关联属性</small></div><button data-edit-tag="${esc(t.id)}">编辑</button></div>`).join('');}
function tagEditor(id){const t=id?structuredClone(tag(id)):{id:newId(),name:'',parentId:null,kind:'text',properties:[]};const revision=state.revision;modal(`<h2>${id?'编辑':'新建'}标签</h2><label class="field"><span>名称</span><input id="tag-name" required maxlength="80" value="${esc(t.name)}"></label><label class="field"><span>父标签</span><select id="tag-parent"><option value="">无</option>${state.tags.filter(x=>x.id!==t.id).map(x=>`<option value="${esc(x.id)}" ${x.id===t.parentId?'selected':''}>${esc(x.name)}</option>`).join('')}</select></label><label class="field"><span>作为属性名时的值类型</span><select id="tag-kind">${Object.entries(kinds).map(([k,v])=>`<option value="${k}" ${k===t.kind?'selected':''}>${v}</option>`).join('')}</select></label><h3>此类型标签对应的属性</h3><div class="checks">${state.tags.map(x=>`<label><input type="checkbox" data-schema="${esc(x.id)}" ${t.properties.includes(x.id)?'checked':''}>${esc(x.name)}</label>`).join('')}</div><p class="muted">已有值不兼容时，会拒绝修改类型；父标签不能循环。</p>`,async()=>{t.name=$('#tag-name').value.trim();t.parentId=$('#tag-parent').value||null;t.kind=$('#tag-kind').value;t.properties=[...document.querySelectorAll('[data-schema]:checked')].map(e=>e.dataset.schema);const r=await api('/tags','POST',{revision,tag:t});setCatalog(r.catalog);await render();notify('标签已保存');});}
async function save(approve=false){collect();const isNew=!item(edit.id);const r=await api(isNew?'/items':'/items/'+encodeURIComponent(edit.id)+(approve?'/confirm':''),isNew||approve?'POST':'PUT',{revision:saveRevision,item:edit});setCatalog(r.catalog);remember([r.item]);dirty=false;selected=r.item.id;edit=structuredClone(r.item);saveRevision=state.revision;if(approve||isNew){view='items';filter='';page=1;$('#search').value='';}await render();renderDetail();notify(approve?'已确认入库':'已保存');}
async function batch(action,addTags=[],removeTags=[]){if(!checked.size)throw Error('请先选择候选。');if(!leave())return;setCatalog(await api('/drafts/batch','POST',{versions:Object.fromEntries(checked),action,addTags,removeTags}));checked.clear();dirty=false;edit=null;selected=null;$('#detail').innerHTML='';document.body.classList.remove('detail-open');await render();notify('批量操作完成');}
async function pollJobs(){
 try{const jobs=await api('/imports');let host=$('#import-jobs');if(!host){host=document.createElement('section');host.id='import-jobs';$('#content').before(host);}
 const labels={queued:'排队中',running:'扫描中',cancelling:'正在取消',completed:'完成',cancelled:'已取消',failed:'失败',interrupted:'已中断'};
 host.innerHTML=jobs.slice(-5).reverse().map(j=>`<details><summary>${esc(labels[j.status]||j.status)} · 扫描 ${j.scanned} / 新增 ${j.added} / 跳过 ${j.skipped} / 失败 ${j.failed}</summary><p>${esc(j.path)}</p>${['queued','running'].includes(j.status)?`<button data-cancel-job="${j.id}">取消任务</button>`:['failed','cancelled','interrupted'].includes(j.status)?`<button data-retry-job="${j.id}">重新扫描（跳过已有路径）</button>`:''}<pre>${esc(j.errors.join('\n'))}</pre></details>`).join('');
 if(pollJobs.last!==JSON.stringify(jobs.map(j=>[j.id,j.added,j.status]))){pollJobs.last=JSON.stringify(jobs.map(j=>[j.id,j.added,j.status]));await reloadCatalog();if(!dirty)await render();}
 }catch(e){notify(e.message);}finally{clearTimeout(pollJobs.timer);pollJobs.timer=setTimeout(pollJobs,2000);}
}

$('#mobile-menu').onclick=()=>menu(true);$('#menu-close').onclick=()=>menu(false);$('#menu-backdrop').onclick=()=>menu(false);$('#upload').onclick=uploadFile;
$('#logout').onclick=async()=>{if(!leave())return;try{await api('/logout','POST',{});dirty=false;location.reload();}catch(e){notify(e.message);}};
$('#cancel').onclick=()=>$('#modal').close();$('#search').oninput=()=>{page=1;clearTimeout(render.timer);render.timer=setTimeout(render,150);};$('#toggle').onclick=()=>{list=!list;$('#toggle').textContent=list?'卡片视图':'列表视图';render();};
$('#new').onclick=()=>{if(!leave())return;selected=null;edit={id:newId(),name:'',tags:[],values:{},draft:false,updatedAt:new Date().toISOString()};saveRevision=state.revision;inboundRows=[];inboundTotal=0;dirty=true;renderDetail();$('#item-name').focus();};
$('#import').onclick=()=>{if(!leave())return;const revision=state.revision;modal('<h2>导入本地文件</h2><p class="muted">填写运行后端的电脑上的文件或文件夹绝对路径。不会复制、移动或删除原文件。</p><label class="field"><span>文件 / 文件夹路径</span><input id="import-path" required placeholder="D:\\Movies"></label><label><input id="recursive" type="checkbox" checked> 包含子文件夹</label><p class="muted">后台扫描并分批暂存，可查看进度或取消。达到收藏库容量上限时停止。</p>',async()=>{await api('/imports','POST',{revision,path:$('#import-path').value,recursive:$('#recursive').checked});view='drafts';filter='';page=1;$('#search').value='';dirty=false;edit=null;selected=null;document.body.classList.remove('detail-open');await render();notify('导入任务已加入队列');await pollJobs();});};
$('#detail').addEventListener('input',e=>{
 if(!e.target.dataset.refSearch){dirty=true;return;}
 const key=e.target.dataset.refSearch,query=e.target.value,current=edit.id;clearTimeout(e.target.searchTimer);
 e.target.searchTimer=setTimeout(async()=>{try{const r=await api('/items?limit=30&q='+encodeURIComponent(query));if(edit?.id!==current||e.target.value!==query)return;remember(r.items);const select=[...document.querySelectorAll('[data-ref]')].find(el=>el.dataset.ref===key);if(select)select.innerHTML='<option value="">添加引用…</option>'+r.items.filter(i=>i.id!==edit.id&&!(edit.values[key]?.refs||[]).includes(i.id)).map(i=>`<option value="${esc(i.id)}">${esc(i.name)}</option>`).join('');}catch(err){notify(err.message);}},200);
});
document.addEventListener('change',e=>{if(e.target.dataset.select){const id=e.target.dataset.select;if(e.target.checked)checked.set(id,item(id).version);else checked.delete(id);$('#selection-count').textContent='已选 '+checked.size+' 项';}});
$('#detail').addEventListener('change',e=>{if(e.target.dataset.tagCheck){collect();dirty=true;renderDetail();}if(e.target.id==='add-prop'&&e.target.value){collect();edit.values[e.target.value]={refs:[]};dirty=true;renderDetail();}if(e.target.dataset.ref&&e.target.value){collect();const k=e.target.dataset.ref;const v=edit.values[k]||{refs:[]};v.refs=tag(k).kind==='ref'?[e.target.value]:[...v.refs,e.target.value];edit.values[k]=v;dirty=true;renderDetail();}});
document.addEventListener('click',async e=>{const b=e.target.closest('button');if(!b)return;try{
 if(b.id==='detail-back'){closeDetail();return;}
 if(b.dataset.item||b.dataset.jump){await openItem(b.dataset.item||b.dataset.jump);return;}
 if(b.dataset.view||b.dataset.filter){if(!leave())return;dirty=false;edit=null;selected=null;menu(false);document.body.classList.remove('detail-open');view=b.dataset.view||'items';filter=b.dataset.filter||'';page=1;$('#detail').innerHTML='<p class="muted">选择收藏以查看详情。</p>';render();}
 if(b.id==='more'){page++;await render();}
 if(b.id==='previous'){page=Math.max(1,page-1);await render();}
 if(b.id==='select-page'){for(const i of state.items)checked.set(i.id,i.version);await render();}
 if(b.id==='clear-selection'){checked.clear();await render();}
 if(b.id==='batch-confirm'){await batch('confirm');}
 if(b.id==='batch-discard'&&confirm('放弃所选候选？不会删除原文件。')){await batch('discard');}
 if(b.id==='batch-tags'){if(!checked.size)throw Error('请先选择候选。');modal('<h2>批量调整标签</h2>'+state.tags.map(t=>`<label class="field"><span>${esc(t.name)}</span><select data-batch-tag="${esc(t.id)}"><option value="">保持</option><option value="add">添加</option><option value="remove">移除</option></select></label>`).join(''),async()=>{const fields=[...document.querySelectorAll('[data-batch-tag]')];await batch('tags',fields.filter(e=>e.value==='add').map(e=>e.dataset.batchTag),fields.filter(e=>e.value==='remove').map(e=>e.dataset.batchTag));});}
 if(b.dataset.cancelJob){await api('/imports/'+b.dataset.cancelJob+'/cancel','POST',{});await pollJobs();}
 if(b.dataset.retryJob){await api('/imports/'+b.dataset.retryJob+'/retry','POST',{});await pollJobs();}
 if(b.id==='inbound-more'){collect();inboundPage=(inboundPage+1)*60>=inboundTotal?0:inboundPage+1;const r=await api('/items/'+encodeURIComponent(edit.id)+'/inbound?offset='+inboundPage*60);inboundRows=r.items;remember(r.items);renderDetail();}
 if(b.id==='save'||b.id==='approve'){b.disabled=true;try{await save(b.id==='approve');}finally{b.disabled=false;}}
 if(b.id==='delete'){if(!item(edit.id)){edit=null;dirty=false;document.body.classList.remove('detail-open');$('#detail').innerHTML='';return;}if(confirm('删除这条记录？不会删除原始文件。')){const r=await api('/items/'+encodeURIComponent(edit.id)+'?revision='+saveRevision+'&version='+edit.version,'DELETE');setCatalog(r.catalog);cache.delete(edit.id);dirty=false;edit=null;selected=null;document.body.classList.remove('detail-open');$('#detail').innerHTML='';render();}}
 if(b.dataset.remove){collect();delete edit.values[b.dataset.remove];dirty=true;renderDetail();}
 if(b.dataset.unref){collect();edit.values[b.dataset.unref].refs.splice(Number(b.dataset.index),1);dirty=true;renderDetail();}
 if(b.dataset.up){collect();const a=edit.values[b.dataset.up].refs,n=Number(b.dataset.index);if(n>0)[a[n-1],a[n]]=[a[n],a[n-1]];dirty=true;renderDetail();}
 if(b.id==='new-tag'||b.dataset.editTag)tagEditor(b.dataset.editTag);
 if(b.id==='locate')await api('/items/'+encodeURIComponent(edit.id)+'/locate','POST',{});
 }catch(e){notify(e.message);}});
window.addEventListener('beforeunload',e=>{if(dirty){e.preventDefault();e.returnValue='';}});
refresh().catch(e=>notify('启动失败：'+e.message));
